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
        private static readonly bool TraceBootFatalWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_BOOT_FATAL_WINDOW"), "1", StringComparison.Ordinal);
        private static int _traceBootFatalWindowCount = 0;
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
        private static readonly bool TraceSm64QueueWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SM64_QUEUE_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceSm64QueueWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_SM64_QUEUE_WINDOW_LIMIT", 400);
        private static int _traceSm64QueueWindowCount = 0;
        private static readonly bool TraceSm64DispatchWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SM64_DISPATCH_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceSm64DispatchWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_SM64_DISPATCH_WINDOW_LIMIT", 64);
        private static int _traceSm64DispatchWindowCount = 0;
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
        private static readonly bool TraceViSwapWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_SWAP_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceViSwapWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_VI_SWAP_WINDOW_LIMIT", 400);
        private static int _traceViSwapWindowCount = 0;
        private static readonly bool TraceViProducerWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_PRODUCER_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceViProducerWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_VI_PRODUCER_WINDOW_LIMIT", 200);
        private static readonly uint TraceViProducerWindowStart = ParseTracePc("EUTHERDRIVE_TRACE_N64_VI_PRODUCER_WINDOW_START", 0x80000740u);
        private static readonly uint TraceViProducerWindowEnd = ParseTracePc("EUTHERDRIVE_TRACE_N64_VI_PRODUCER_WINDOW_END", 0x80000790u);
        private static int _traceViProducerWindowCount = 0;
        private static readonly bool TracePcWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PC_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TracePcWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_PC_WINDOW_LIMIT", 256);
        private static readonly uint TracePcWindowStart = ParseTracePc("EUTHERDRIVE_TRACE_N64_PC_WINDOW_START", 0x80000000u);
        private static readonly uint TracePcWindowEnd = ParseTracePc("EUTHERDRIVE_TRACE_N64_PC_WINDOW_END", 0x80000000u);
        private static int _tracePcWindowCount = 0;
        private static readonly bool TraceHotPcSamples =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_HOT_PC"), "1", StringComparison.Ordinal);
        private static readonly int HotPcSampleInterval = Math.Max(1, ParseTraceLimit("EUTHERDRIVE_TRACE_N64_HOT_PC_INTERVAL", 1024));
        private static ulong _hotPcInstructionCounter;
        private static readonly Dictionary<uint, long> HotPcSamples = new Dictionary<uint, long>();
        private static readonly object HotPcSamplesLock = new object();
        private static readonly bool TraceStuckPcDetails =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_STUCK_PC"), "1", StringComparison.Ordinal);
        private static readonly bool TraceExceptionEntry =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_EXCEPTION_ENTRY"), "1", StringComparison.Ordinal);
        private static readonly bool TraceMegaDispatchWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_DISPATCH"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaDispatchWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_DISPATCH_LIMIT", 160);
        private static int _traceMegaDispatchWindowCount = 0;
        private static readonly bool TraceMegaInitWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_INIT_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaInitWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_INIT_WINDOW_LIMIT", 256);
        private static int _traceMegaInitWindowCount = 0;
        private static readonly bool TraceMegaLateWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_LATE_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaLateWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_LATE_WINDOW_LIMIT", 256);
        private static int _traceMegaLateWindowCount = 0;
        private static readonly bool TraceMegaRspBufferWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_RSPBUF_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaRspBufferWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_RSPBUF_WINDOW_LIMIT", 256);
        private static int _traceMegaRspBufferWindowCount = 0;
        private static readonly bool TraceMegaWaitWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_WAIT_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaWaitWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_WAIT_WINDOW_LIMIT", 256);
        private static int _traceMegaWaitWindowCount = 0;
        private static readonly bool TraceMegaIdleWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_IDLE_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaIdleWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_IDLE_WINDOW_LIMIT", 256);
        private static int _traceMegaIdleWindowCount = 0;
        private static readonly bool TraceMegaFatalWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_FATAL_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaFatalWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_FATAL_WINDOW_LIMIT", 256);
        private static int _traceMegaFatalWindowCount = 0;
        private static readonly bool TraceMegaStatusCall =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_STATUS_CALL"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaStatusCallLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_STATUS_CALL_LIMIT", 96);
        private static int _traceMegaStatusCallCount = 0;
        private static readonly bool TraceMegaPiCallbackWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_PI_CALLBACK"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaPiCallbackWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_PI_CALLBACK_LIMIT", 192);
        private static int _traceMegaPiCallbackWindowCount = 0;
        private static readonly bool TraceMegaLowRamWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_LOWRAM_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceMegaLowRamWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_MEGA_LOWRAM_WINDOW_LIMIT", 192);
        private static int _traceMegaLowRamWindowCount = 0;
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
        private const ulong CauseExcCodeSyscall = 8UL << 2;
        private const ulong CauseExcCodeBreak = 9UL << 2;
        private const ulong CauseExcCodeInterrupt = 0UL << 2;
        private const ulong CauseExcCodeRi = 10UL << 2;
        private static readonly bool UnknownOpcodeAsNop =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_UNKNOWN_AS_NOP"), "1", StringComparison.Ordinal);
        private static readonly bool AllowInstructionLowPhysicalFallbackOnTlbMiss =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_STRICT_ITLB"), "1", StringComparison.Ordinal);
        private static readonly bool AlignMisalignedPcDuringBringup =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_ALIGN_MISALIGNED_PC"), "0", StringComparison.Ordinal);
        private static readonly bool UseBootRomHleStartup =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_DISABLE_BOOTROM_HLE"), "1", StringComparison.Ordinal);
        private static readonly bool FastBootChecksumLoop =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FAST_BOOT_CHECKSUM"), "0", StringComparison.Ordinal);
        private static readonly bool FastBootClearLoop =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FAST_BOOT_CLEAR"), "0", StringComparison.Ordinal);
        private static readonly bool FastBootAssetDecode =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FAST_BOOT_ASSET_DECODE"), "0", StringComparison.Ordinal);
        private static readonly bool FastRdramInstructionFetch =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FAST_RDRAM_FETCH"), "0", StringComparison.Ordinal);
        private static readonly bool FastIdleLoop =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FAST_IDLE_LOOP"), "0", StringComparison.Ordinal);
        private static readonly uint IdleLoopFastForwardCycles =
            (uint)Math.Max(2, ParseTraceLimit("EUTHERDRIVE_N64_IDLE_FAST_FORWARD_CYCLES", 65536));
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
        private static ulong _cpuExceptionLogCount;
        private static ulong _tlbRefillLogCount;
        private static ulong _addressErrorLogCount;
        private static bool _loadLinkedActive;

        private static int ParseTraceLimit(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (int.TryParse(raw, out int parsed) && parsed > 0)
                return parsed;

            return fallback;
        }

        private static uint ParseTracePc(string name, uint fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);

            if (uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint parsed))
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
                Registers.R4300.PC = exlAlreadySet || ShouldUseGeneralVectorForTlbRefill()
                    ? 0x80000180u
                    : 0x80000000u;
            }
        }

        private static bool ShouldUseGeneralVectorForTlbRefill()
        {
            if (memory == null)
                return false;

            uint refill0 = memory.ReadUInt32PhysicalFast(0x00000000u);
            uint refill1 = memory.ReadUInt32PhysicalFast(0x00000004u);
            if (LooksLikeExceptionVectorStub(refill0, refill1))
                return false;

            uint general0 = memory.ReadUInt32PhysicalFast(0x00000180u);
            uint general1 = memory.ReadUInt32PhysicalFast(0x00000184u);
            return LooksLikeExceptionVectorStub(general0, general1);
        }

        private static bool LooksLikeExceptionVectorStub(uint firstWord, uint secondWord)
        {
            return ((firstWord & 0xFFFF0000u) == 0x3C1A0000u && (secondWord & 0xFFFF0000u) == 0x275A0000u)
                || firstWord == 0x03400008u
                || firstWord == 0x42000018u
                || (firstWord & 0xFC000000u) == 0x08000000u
                || (firstWord & 0xFC000000u) == 0x0C000000u;
        }

        internal static void RaiseCpuException(ulong exceptionCode, uint faultingPc)
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

            _cpuExceptionLogCount++;
            if (TraceExceptionEntry && (_cpuExceptionLogCount <= 64 || (_cpuExceptionLogCount % 256) == 0))
            {
                uint vectorPc = Registers.R4300.PC;
                uint vec0 = TraceReadWordOrZero(vectorPc);
                uint vec4 = TraceReadWordOrZero(vectorPc + 4u);
                uint vec8 = TraceReadWordOrZero(vectorPc + 8u);
                uint vecC = TraceReadWordOrZero(vectorPc + 12u);
                uint miIntr = memory.ReadUInt32(0xA4300008u);
                uint miMask = memory.ReadUInt32(0xA430000Cu);
                uint viCurrent = memory.ReadUInt32(0xA4400010u);
                uint piStatus = memory.ReadUInt32(0xA4600010u);
                uint siStatus = memory.ReadUInt32(0xA4800018u);
                Common.Logger.PrintWarningLine(
                    $"CPU exception entry (count={_cpuExceptionLogCount}) faultPc=0x{faultingPc:x8} vector=0x{vectorPc:x8} " +
                    $"exc=0x{exceptionCode:x8} exlWasSet={exlAlreadySet} bev={bevSet} delay={inDelaySlot} " +
                    $"epc=0x{Registers.COP0.Reg[Registers.COP0.EPC_REG]:x8} cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8} " +
                    $"status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} badv=0x{Registers.COP0.Reg[Registers.COP0.BADVADDR_REG]:x8} " +
                    $"vec[0]=0x{vec0:x8} vec[4]=0x{vec4:x8} vec[8]=0x{vec8:x8} vec[c]=0x{vecC:x8} " +
                    $"miIntr=0x{miIntr:x8} miMask=0x{miMask:x8} viCurrent=0x{viCurrent:x8} " +
                    $"piStatus=0x{piStatus:x8} siStatus=0x{siStatus:x8}");

                if (_cpuExceptionLogCount <= 16)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Recent PCs before CPU exception:");
                    for (int i = 0; i < 24; i++)
                    {
                        int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                        RecentInst rec = _recentInst[idx];
                        sb.Append($" [{i}]pc=0x{rec.Pc:x8}/op=0x{rec.Op:x8}");
                    }
                    Common.Logger.PrintWarningLine(sb.ToString());
                }
            }

            ClearLoadLinkedReservation();
        }

        internal static void RaiseSyscallException(uint faultingPc)
        {
            RaiseCpuException(CauseExcCodeSyscall, faultingPc);
        }

        internal static void RaiseBreakException(uint faultingPc)
        {
            RaiseCpuException(CauseExcCodeBreak, faultingPc);
        }

        private static void RaiseAddressErrorException(uint badAddress, bool isStore, uint faultingPc)
        {
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
                a0w = TraceReadWordOrZero(a0);
                a0wm8 = TraceReadWordOrZero(a0 - 8u);
                a0wm4 = TraceReadWordOrZero(a0 - 4u);
                a0w4 = TraceReadWordOrZero(a0 + 4u);
                a0w8 = TraceReadWordOrZero(a0 + 8u);
                a0wc = TraceReadWordOrZero(a0 + 12u);
                a0w10 = TraceReadWordOrZero(a0 + 16u);
                v0w = TraceReadWordOrZero(v0);
                v0w4 = TraceReadWordOrZero(v0 + 4u);
                v0w8 = TraceReadWordOrZero(v0 + 8u);
                v0wc = TraceReadWordOrZero(v0 + 12u);
                v0w10 = TraceReadWordOrZero(v0 + 16u);
                v0wm4 = TraceReadWordOrZero(v0 - 4u);

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
            ulong cause = RefreshRcpInterruptPending();
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

        internal static ulong RefreshRcpInterruptPending()
        {
            // N64 RCP interrupts are routed through MI and appear on CP0 IP2.
            ulong cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
            uint miIntr = memory.ReadUInt32(0xA4300008u);
            uint miMask = memory.ReadUInt32(0xA430000Cu);
            bool rcpPending = (miIntr & miMask & 0x3Fu) != 0;

            // Only control IP2 from MI; preserve all other pending IP bits (timer/SW/etc).
            // Mupen clears ExcCode when an interrupt is made pending, so guest code which
            // reads Cause while EXL/IE temporarily blocks delivery still sees Int semantics.
            cause = rcpPending
                ? ((cause | CauseIp2Bit) & ~CauseExcCodeMask)
                : (cause & ~CauseIp2Bit);
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = cause;
            return cause;
        }

        private static bool CanTraceReadWord(ulong address)
        {
            uint addr32 = (uint)address;
            uint segment = addr32 & 0xE0000000u;
            return segment == 0x80000000u
                || segment == 0xA0000000u
                || segment == 0xC0000000u;
        }

        private static uint TraceReadWordOrZero(ulong address)
        {
            if (!CanTraceReadWord(address))
                return 0;

            try
            {
                return memory.ReadUInt32((uint)address);
            }
            catch
            {
                return 0;
            }
        }

        internal static bool CheckPendingInterruptsNow(uint pc)
        {
            return ServiceInterrupts(pc);
        }

        internal static void SetLoadLinkedReservation(uint address)
        {
            uint aligned = address & 0xFFFFFFFCu;
            _loadLinkedActive = true;
            Registers.COP0.Reg[Registers.COP0.LLADDR_REG] = aligned;
        }

        internal static bool TryStoreConditional(uint address)
        {
            _ = address;
            bool success = _loadLinkedActive;
            _loadLinkedActive = false;
            return success;
        }

        internal static void ClearLoadLinkedReservation()
        {
            _loadLinkedActive = false;
        }

        private static uint Reg32(int reg)
        {
            return (uint)Registers.R4300.Reg[reg];
        }

        private static ulong SignExtend32(uint value)
        {
            return unchecked((ulong)(long)(int)value);
        }

        private static void SetReg32(int reg, uint value)
        {
            Registers.R4300.Reg[reg] = SignExtend32(value);
        }

        private static void AddSyntheticCycles(uint cycles)
        {
            CycleCounter += cycles;
            Count += cycles;
            memory?.Tick(cycles);

            uint previousCount = (uint)Registers.COP0.Reg[Registers.COP0.COUNT_REG];
            uint newCount = (uint)(Count >> 1);
            Registers.COP0.Reg[Registers.COP0.COUNT_REG] = newCount;
            uint compare = (uint)Registers.COP0.Reg[Registers.COP0.COMPARE_REG];
            if (CountCompareReached(previousCount, newCount, compare))
                Registers.COP0.Reg[Registers.COP0.CAUSE_REG] |= CauseIp7Bit;
            Common.Measure.CycleCounter = CycleCounter;
        }

        private static bool TryFastForwardBootChecksumLoop(uint pc)
        {
            if (!FastBootChecksumLoop || pc != 0x80000184u)
                return false;

            uint t0 = Reg32(8);
            uint ra = Reg32(31);
            uint t1 = Reg32(9);
            uint s6 = Reg32(22);
            if (ra != 0x00100000u || t0 >= ra || t1 < 0x80000400u || t1 >= 0x80100000u)
                return false;

            ulong a2 = Registers.R4300.Reg[6];
            ulong a3 = Registers.R4300.Reg[7];
            ulong t2 = Registers.R4300.Reg[10];
            ulong t3 = Registers.R4300.Reg[11];
            ulong t4 = Registers.R4300.Reg[12];
            ulong t5 = Registers.R4300.Reg[13];
            ulong s0 = Registers.R4300.Reg[16];
            ulong v0 = Registers.R4300.Reg[2];
            ulong v1 = Registers.R4300.Reg[3];
            ulong a0 = Registers.R4300.Reg[4];
            ulong a1 = Registers.R4300.Reg[5];
            ulong t6 = Registers.R4300.Reg[14];
            ulong t7 = Registers.R4300.Reg[15];
            ulong t8 = Registers.R4300.Reg[24];
            ulong t9 = Registers.R4300.Reg[25];
            uint iterations = 0;
            while (t0 < ra)
            {
                v0 = SignExtend32(memory.ReadUInt32PhysicalFast(t1));
                v1 = SignExtend32(unchecked((uint)a3 + (uint)v0));
                bool carry = v1 < a3;
                a1 = v1;
                if (carry)
                    t2 = SignExtend32(unchecked((uint)t2 + 1u));

                v1 = v0 & 0x1Fu;
                t7 = SignExtend32(unchecked((uint)t5 - (uint)v1));
                t8 = SignExtend32((uint)v0 >> (int)(t7 & 0x1Fu));
                t6 = SignExtend32((uint)v0 << (int)(v1 & 0x1Fu));
                a0 = t6 | t8;

                bool a2LessThanV0 = a2 < v0;
                a3 = a1;
                t3 ^= v0;
                s0 = SignExtend32(unchecked((uint)s0 + (uint)a0));
                if (a2LessThanV0)
                {
                    t9 = a3 ^ v0;
                    a2 = t9 ^ a2;
                }
                else
                {
                    a2 ^= a0;
                }

                ulong table = SignExtend32(memory.ReadUInt32PhysicalFast(s6));
                t0 = unchecked(t0 + 4u);
                s6 = unchecked(s6 + 4u);
                t7 = v0 ^ table;
                t4 = SignExtend32(unchecked((uint)t7 + (uint)t4));
                t1 = unchecked(t1 + 4u);
                s6 &= 0xA00002FFu;
                iterations++;
            }

            Registers.R4300.Reg[2] = v0;
            Registers.R4300.Reg[3] = v1;
            Registers.R4300.Reg[4] = a0;
            Registers.R4300.Reg[5] = a1;
            Registers.R4300.Reg[6] = a2;
            Registers.R4300.Reg[7] = a3;
            SetReg32(8, t0);
            SetReg32(9, t1);
            Registers.R4300.Reg[10] = t2;
            Registers.R4300.Reg[11] = t3;
            Registers.R4300.Reg[12] = t4;
            Registers.R4300.Reg[13] = t5;
            Registers.R4300.Reg[14] = t6;
            Registers.R4300.Reg[15] = t7;
            Registers.R4300.Reg[16] = s0;
            SetReg32(22, s6);
            Registers.R4300.Reg[24] = t8;
            Registers.R4300.Reg[25] = t9;
            Registers.R4300.PC = 0x8000018Cu;
            AddSyntheticCycles(iterations * 24u);
            Common.Measure.InstructionCount += iterations * 24UL;
            return true;
        }

        private static bool TryFastForwardBootClearLoop(uint pc)
        {
            if (!FastBootClearLoop || pc != 0x80003074u)
                return false;

            if (memory.ReadUInt32PhysicalFast(0x00003074u) != 0x24840020u
                || memory.ReadUInt32PhysicalFast(0x00003078u) != 0xAC80FFE0u
                || memory.ReadUInt32PhysicalFast(0x0000307Cu) != 0xAC80FFE4u
                || memory.ReadUInt32PhysicalFast(0x00003080u) != 0xAC80FFE8u
                || memory.ReadUInt32PhysicalFast(0x00003084u) != 0xAC80FFECu
                || memory.ReadUInt32PhysicalFast(0x00003088u) != 0xAC80FFF0u
                || memory.ReadUInt32PhysicalFast(0x0000308Cu) != 0xAC80FFF4u
                || memory.ReadUInt32PhysicalFast(0x00003090u) != 0xAC80FFF8u
                || memory.ReadUInt32PhysicalFast(0x00003094u) != 0x1487FFF7u)
            {
                return false;
            }

            uint startAddress = Reg32(4);
            uint endAddress = Reg32(7);
            uint start = startAddress & 0x1FFFFFFFu;
            uint end = endAddress & 0x1FFFFFFFu;
            if ((startAddress & 0xE0000000u) != 0x80000000u
                || (endAddress & 0xE0000000u) != 0x80000000u
                || end <= start
                || end > memory.RDRAM.Length
                || ((start | end) & 0x1Fu) != 0)
            {
                return false;
            }

            Array.Clear(memory.RDRAM, (int)start, (int)(end - start));
            uint iterations = (end - start) >> 5;
            SetReg32(4, endAddress);
            Registers.R4300.PC = 0x8000309Cu;
            AddSyntheticCycles(iterations * 10u);
            Common.Measure.InstructionCount += iterations * 10UL;
            return true;
        }

        private static bool TryFastForwardBootAssetDecode(uint pc)
        {
            if (!FastBootAssetDecode || pc != 0x800012C4u)
                return false;

            if (memory.ReadUInt32PhysicalFast(0x000012C0u) != 0x01D0A021u
                || memory.ReadUInt32PhysicalFast(0x000012C4u) != 0x54C0000Fu
                || memory.ReadUInt32PhysicalFast(0x000012FCu) != 0x24060008u
                || memory.ReadUInt32PhysicalFast(0x00001300u) != 0x30F90080u
                || memory.ReadUInt32PhysicalFast(0x00001304u) != 0x13200006u
                || memory.ReadUInt32PhysicalFast(0x00001320u) != 0x92230000u
                || memory.ReadUInt32PhysicalFast(0x00001324u) != 0x92290001u
                || memory.ReadUInt32PhysicalFast(0x00001338u) != 0x012B2025u
                || memory.ReadUInt32PhysicalFast(0x0000133Cu) != 0x14A00005u
                || memory.ReadUInt32PhysicalFast(0x000013B8u) != 0x1614FFC2u)
            {
                return false;
            }

            uint dst = Reg32(16);
            uint src = Reg32(17);
            uint end = Reg32(20);
            uint chunkLimitPointer = Reg32(18);
            if ((dst & 0xE0000000u) != 0x80000000u
                || (src & 0xE0000000u) != 0x80000000u
                || (end & 0xE0000000u) != 0x80000000u
                || (chunkLimitPointer & 0xE0000000u) != 0x80000000u
                || end <= dst
                || end - dst > 0x400000u)
            {
                return false;
            }

            uint dstPhys = dst & 0x1FFFFFFFu;
            uint srcPhys = src & 0x1FFFFFFFu;
            uint endPhys = end & 0x1FFFFFFFu;
            if (dstPhys >= memory.RDRAM.Length
                || endPhys > memory.RDRAM.Length
                || srcPhys >= memory.RDRAM.Length
                || endPhys <= dstPhys)
            {
                return false;
            }

            uint flags = Reg32(7) & 0xFFu;
            uint bitsRemaining = Reg32(6) & 0xFFu;
            uint initialDstPhys = dstPhys;
            uint tokens = 0;
            byte[] rdram = memory.RDRAM;
            bool completed = false;

            while (dstPhys < endPhys)
            {
                if (bitsRemaining == 0)
                {
                    uint chunkLimit;
                    try
                    {
                        chunkLimit = memory.ReadUInt32(chunkLimitPointer);
                    }
                    catch
                    {
                        return false;
                    }

                    if (chunkLimit < (0x80000000u | srcPhys))
                        break;

                    if (srcPhys >= rdram.Length)
                        return false;

                    flags = rdram[srcPhys++];
                    bitsRemaining = 8;
                }

                bool literal = (flags & 0x80u) != 0;
                flags = (flags << 1) & 0xFFu;

                if (literal)
                {
                    if (srcPhys >= rdram.Length)
                        return false;

                    rdram[dstPhys++] = rdram[srcPhys++];
                }
                else
                {
                    if (srcPhys + 1u >= rdram.Length)
                        return false;

                    uint b0 = rdram[srcPhys++];
                    uint b1 = rdram[srcPhys++];
                    uint displacement = ((b0 & 0x0Fu) << 8) | b1;
                    uint length = b0 >> 4;
                    if (length == 0)
                    {
                        if (srcPhys >= rdram.Length)
                            return false;

                        length = (uint)rdram[srcPhys++] + 0x12u;
                    }
                    else
                    {
                        length += 2u;
                    }

                    if (displacement + 1u > dstPhys)
                        return false;

                    uint copyPhys = dstPhys - displacement - 1u;
                    for (uint i = 0; i < length && dstPhys < endPhys; i++)
                    {
                        if (copyPhys >= rdram.Length)
                            return false;

                        rdram[dstPhys++] = rdram[copyPhys++];
                    }
                }

                bitsRemaining--;
                tokens++;
            }

            if (dstPhys >= endPhys)
                completed = true;

            if (!completed && dstPhys == initialDstPhys)
                return false;

            SetReg32(6, bitsRemaining);
            SetReg32(7, flags);
            SetReg32(16, completed ? end : (0x80000000u | dstPhys));
            SetReg32(17, 0x80000000u | srcPhys);

            if (completed)
            {
                uint stack = Reg32(29);
                if ((stack & 0xE0000000u) == 0x80000000u || (stack & 0xE0000000u) == 0xA0000000u)
                    memory.WriteUInt32(stack + 0x30u, flags);
            }

            Registers.R4300.PC = completed ? 0x800013C0u : 0x800012C4u;
            uint produced = dstPhys - initialDstPhys;
            AddSyntheticCycles(Math.Max(1u, produced * 4u));
            Common.Measure.InstructionCount += Math.Max(tokens * 8UL, produced);
            return true;
        }

        private static bool TryFastForwardIdleLoop(uint pc)
        {
            if (!FastIdleLoop || (pc != 0x80000810u && pc != 0x80000814u))
                return false;

            if (memory.ReadUInt32PhysicalFast(0x00000810u) != 0x1000FFFFu
                || memory.ReadUInt32PhysicalFast(0x00000814u) != 0x00000000u)
            {
                return false;
            }

            Registers.R4300.PC = 0x80000810u;
            AddSyntheticCycles(IdleLoopFastForwardCycles);
            Common.Measure.InstructionCount += IdleLoopFastForwardCycles >> 1;
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

        private static void TrackHotPcSample(uint pc)
        {
            if (!TraceHotPcSamples)
                return;

            _hotPcInstructionCounter++;
            if ((_hotPcInstructionCounter % (ulong)HotPcSampleInterval) != 0)
                return;

            lock (HotPcSamplesLock)
            {
                if (!HotPcSamples.TryGetValue(pc, out long count))
                    count = 0;
                HotPcSamples[pc] = count + 1;
            }
        }

        public static string GetHotPcSummary(int topN = 8)
        {
            if (!TraceHotPcSamples)
                return string.Empty;

            lock (HotPcSamplesLock)
                return FormatTopUnknownSummary(HotPcSamples, "pc", topN);
        }

        private static bool CountCompareReached(uint previousCount, uint newCount, uint compare)
        {
            // compare match between previousCount(exclusive) -> newCount(inclusive), wrapping at 32-bit.
            if (previousCount <= newCount)
                return compare > previousCount && compare <= newCount;
            return compare > previousCount || compare <= newCount;
        }

        internal static void SyncCountRegisterWrite(uint countValue)
        {
            Count = ((ulong)countValue) << 1;
            Registers.COP0.Reg[Registers.COP0.COUNT_REG] = countValue;
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

        private const ulong IPL3_SUM_NUS_5101 = 0x000000A5F80BF620UL;
        private const ulong IPL3_SUM_NUS_6101 = 0x000000D0027FDF31UL;
        private const ulong IPL3_SUM_NUS_6101_ALT = 0x000000CFFB631223UL;
        private const ulong IPL3_SUM_NUS_6102 = 0x000000D057C85244UL;
        private const ulong IPL3_SUM_NUS_6103 = 0x000000D6497E414BUL;
        private const ulong IPL3_SUM_NUS_6105 = 0x0000011A49F60E96UL;
        private const ulong IPL3_SUM_NUS_6106 = 0x000000D6D5BE5580UL;
        private const ulong IPL3_SUM_NUS_5167 = 0x000001053BC19870UL;
        private const ulong IPL3_SUM_NUS_8303 = 0x000000D2E53EF008UL;
        private const ulong IPL3_SUM_NUS_8401 = 0x000000D2E53EF39FUL;
        private const ulong IPL3_SUM_NUS_8501 = 0x000000D2E53E5DDAUL;

        private static ulong SumIpl3Words(uint startAddress, int length)
        {
            ulong sum = 0;
            int wordCount = length >> 2;

            for (int i = 0; i < wordCount; ++i)
                sum += memory.ReadUInt32(startAddress + ((uint)i << 2));

            return sum;
        }

        private static uint GetCICSeed()
        {
            // Use kseg1 alias for cart ROM reads during early boot.
            // Data-side TLB may not be initialized yet.
            const uint cartBootBaseKseg1 = 0xB0000040u;
            ulong ipl3Sum = SumIpl3Words(cartBootBaseKseg1, 0xFC0);

            switch (ipl3Sum)
            {
                case IPL3_SUM_NUS_5101:
                    return CIC_SEED_NUS_5101;

                case IPL3_SUM_NUS_6101:
                case IPL3_SUM_NUS_6101_ALT:
                    return CIC_SEED_NUS_6101;

                case IPL3_SUM_NUS_6102:
                    return CIC_SEED_NUS_6102;

                case IPL3_SUM_NUS_6103:
                    return CIC_SEED_NUS_6103;

                case IPL3_SUM_NUS_6105:
                    return CIC_SEED_NUS_6105;

                case IPL3_SUM_NUS_6106:
                    return CIC_SEED_NUS_6106;

                case IPL3_SUM_NUS_5167:
                case IPL3_SUM_NUS_8303:
                case IPL3_SUM_NUS_8401:
                case IPL3_SUM_NUS_8501:
                    return CIC_SEED_NUS_8303;
            }

            uint CRC        = CRC32(cartBootBaseKseg1, 0xFC0);
            uint Aleck64CRC = CRC32(cartBootBaseKseg1, 0xBC0);

            if (Aleck64CRC == CRC_NUS_5101) return CIC_SEED_NUS_5101;
            switch (CRC)
            {
                default:
                    Common.Logger.PrintWarningLine(
                        $"Unknown CIC (ipl3sum=0x{ipl3Sum:x16}, crc=0x{CRC:x8}), defaulting to seed CIC-6101.");
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

        private static uint GetBootSeedByte(uint cicSeed)
        {
            return (cicSeed >> 8) & 0xFFu;
        }

        public static void InterpretOpcode(uint Opcode)
        {
            if (Registers.R4300.Reg[0] != 0) Registers.R4300.Reg[0] = 0;
            if (TraceHotPcSamples)
                TrackHotPcSample(Registers.R4300.PC);

            if (Registers.COP0.Reg[Registers.COP0.COUNT_REG] >= 0xFFFFFFFF)
            {
                Registers.COP0.Reg[Registers.COP0.COUNT_REG] = 0x0;
                Count = 0x0;
            }

            OpcodeTable.OpcodeDesc Desc = new OpcodeTable.OpcodeDesc(Opcode);
            OpcodeTable.InstInfo   Info = OpcodeTable.GetOpcodeInfo(Opcode);

            if (TraceSm64DispatchWindow
                && _traceSm64DispatchWindowCount < TraceSm64DispatchWindowLimit
                && Registers.R4300.PC >= 0x80322E10u
                && Registers.R4300.PC <= 0x80322E20u)
            {
                _traceSm64DispatchWindowCount++;
                Console.WriteLine(
                    $"[N64DISPATCH] #{_traceSm64DispatchWindowCount} pc=0x{Registers.R4300.PC:x8} op=0x{Opcode:x8} " +
                    $"handler={Info.Interpret.Method.Name} asm='{Info.FormattedASM}' " +
                    $"rs=0x{Registers.R4300.Reg[Desc.op1]:x16} rt=0x{Registers.R4300.Reg[Desc.op2]:x16}");
            }

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

            uint cicSeed = GetCICSeed();

            if (UseBootRomHleStartup)
            {
                // Match Mupen/CEN64-style boot HLE bring-up: start from a clean CPU state
                // and let the HLE path seed only the registers IPL3 is documented to need.
                Registers.R4300.HI = 0;
                Registers.R4300.LO = 0;
                Registers.R4300.PC = 0xA4000040;
            }
            else
            {
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
                Registers.R4300.Reg[22] = GetBootSeedByte(cicSeed);
                Registers.R4300.Reg[23] = 0;
                Registers.R4300.Reg[25] = 0xFFFFFFFF9DEBB54F;
                Registers.R4300.Reg[29] = 0xFFFFFFFFA4001FF0;
                Registers.R4300.Reg[31] = 0xFFFFFFFFA4001550;
                Registers.R4300.HI      = 0x000000003FC18657;
                Registers.R4300.LO      = 0x000000003103E121;
                Registers.R4300.PC      = 0xA4000040;
            }

            memory.FastMemoryCopy(0xA4000000, 0xB0000000, 0x1000); // Load the 4 KiB IPL3 boot code into SP memory.

            // PIF RAM[0x24] carries reset metadata and control flags start cleared.
            uint pif24 =
                (((RomType & 0x1u) << 19)
                | ((0u & 0x1u) << 18) // s7
                | ((ResetType & 0x1u) << 17)
                | (GetBootSeedByte(cicSeed) << 8)
                | 0x3Fu);
            memory.WriteUInt32(0xBFC007E4u, pif24);
            memory.WriteUInt8(0xBFC007FFu, 0x00);

            TLB.Reset();
            COP0.PowerOnCOP0();
            SyncCountRegisterWrite((uint)Registers.COP0.Reg[Registers.COP0.COUNT_REG]);
            COP1.PowerOnCOP1();
            if (UseBootRomHleStartup)
                ApplyBootRomHleStartup(RomType, ResetType, TVType, cicSeed);

            R4300_ON = true;
            UnknownOpcodeCount = 0;
            lock (UnknownOpcodeLock)
            {
                UnknownOpcodeByPc.Clear();
                UnknownOpcodeByValue.Clear();
            }
            lock (HotPcSamplesLock)
            {
                HotPcSamples.Clear();
                _hotPcInstructionCounter = 0;
            }
            _stuckPcLogCount = 0;
            _loggedPifTailEntry = false;
            _loggedFirstBfcEntry = false;
            _tlbRefillLogCount = 0;
            _traceEretWindowCount = 0;
            _traceRefillWindowCount = 0;
            _traceEarlyLoopWindowCount = 0;
            _traceSm64WalkWindowCount = 0;
            _traceSm64QueueWindowCount = 0;
            _traceSm64DispatchWindowCount = 0;
            _traceMegaDispatchWindowCount = 0;
            _traceMegaInitWindowCount = 0;
            _traceMegaLateWindowCount = 0;
            _traceMegaWaitWindowCount = 0;
            _traceMegaFatalWindowCount = 0;
            _traceMegaStatusCallCount = 0;
            _traceMegaPiCallbackWindowCount = 0;
            _traceMegaLowRamWindowCount = 0;
            _tracePcWindowCount = 0;
            ClearLoadLinkedReservation();

            StartCpuThread();
        }

        public static void ResumeR4300()
        {
            StopR4300();
            R4300_ON = true;
            StartCpuThread();
        }

        private static void StartCpuThread()
        {
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
                        if (TryFastForwardBootChecksumLoop(pc))
                            continue;
                        if (TryFastForwardBootClearLoop(pc))
                            continue;
                        if (TryFastForwardBootAssetDecode(pc))
                            continue;
                        if (TryFastForwardIdleLoop(pc))
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
                            uint fetchPhysical = 0;
                            bool haveFetchPhysical = false;
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
                                fetchPhysical = translated;
                                haveFetchPhysical = true;
                                fetchAddress = 0xA0000000u | translated;
                            }
                            else
                            {
                                fetchPhysical = pc & 0x1FFFFFFFu;
                                haveFetchPhysical = true;
                            }

                            uint Opcode;
                            if (!FastRdramInstructionFetch
                                || !haveFetchPhysical
                                || !memory.TryReadRdramUInt32PhysicalFast(fetchPhysical, out Opcode))
                            {
                                Opcode = memory.ReadUInt32(fetchAddress);
                            }
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
                                t0w = TraceReadWordOrZero(t0);
                                t1w = TraceReadWordOrZero(t1);
                                t1w4 = TraceReadWordOrZero(t1 + 4u);

                                Console.WriteLine(
                                    $"[N64EARLY] #{_traceEarlyLoopWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} " +
                                    $"t6=0x{t6:x16} t7=0x{t7:x16} t8=0x{t8:x16} t9=0x{t9:x16} " +
                                    $"[t0]=0x{t0w:x8} [t1]=0x{t1w:x8} [t1+4]=0x{t1w4:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8}");
                            }

                            if (TraceBootFatalWindow
                                && _traceBootFatalWindowCount < 160
                                && pc >= 0x800001C0
                                && pc <= 0x80000250)
                            {
                                _traceBootFatalWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong s4 = Registers.R4300.Reg[20];
                                ulong s5 = Registers.R4300.Reg[21];
                                ulong s6 = Registers.R4300.Reg[22];
                                ulong s7 = Registers.R4300.Reg[23];
                                uint bootPif24 = TraceReadWordOrZero(0xBFC007E4u);
                                uint low300 = TraceReadWordOrZero(0x80000300u);
                                uint low304 = TraceReadWordOrZero(0x80000304u);
                                uint low30c = TraceReadWordOrZero(0x8000030Cu);
                                uint low314 = TraceReadWordOrZero(0x80000314u);
                                uint low318 = TraceReadWordOrZero(0x80000318u);
                                uint t0w = TraceReadWordOrZero(t0);
                                uint t1w = TraceReadWordOrZero(t1);
                                uint a0w = TraceReadWordOrZero(a0);
                                uint v0w = TraceReadWordOrZero(v0);

                                Console.WriteLine(
                                    $"[N64BOOTFATAL] #{_traceBootFatalWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} " +
                                    $"s3=0x{s3:x16} s4=0x{s4:x16} s5=0x{s5:x16} s6=0x{s6:x16} s7=0x{s7:x16} " +
                                    $"[a0]=0x{a0w:x8} [v0]=0x{v0w:x8} [t0]=0x{t0w:x8} [t1]=0x{t1w:x8} " +
                                    $"pif24=0x{bootPif24:x8} low300=0x{low300:x8} low304=0x{low304:x8} low30c=0x{low30c:x8} " +
                                    $"low314=0x{low314:x8} low318=0x{low318:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} siStatus=0x{memory.ReadUInt32(0x04800018):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceEretWindow
                                && _traceEretWindowCount < TraceEretWindowLimit
                                && pc >= 0x80327de0
                                && pc <= 0x80327ec0)
                            {
                                _traceEretWindowCount++;
                                ulong k0 = Registers.R4300.Reg[26];
                                ulong k1 = Registers.R4300.Reg[27];
                                uint m118 = 0;
                                uint m11c = 0;
                                try
                                {
                                    m118 = TraceReadWordOrZero(k0 + 0x118u);
                                    m11c = TraceReadWordOrZero(k0 + 0x11Cu);
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
                                a0w = TraceReadWordOrZero(a0);
                                a0w4 = TraceReadWordOrZero(a0 + 4u);
                                a0w8 = TraceReadWordOrZero(a0 + 8u);
                                a0wc = TraceReadWordOrZero(a0 + 12u);
                                t8w = TraceReadWordOrZero(t8);
                                t8w4 = TraceReadWordOrZero(t8 + 4u);
                                t8w8 = TraceReadWordOrZero(t8 + 8u);
                                t8wc = TraceReadWordOrZero(t8 + 12u);
                                v0w = TraceReadWordOrZero(v0);
                                v0w4 = TraceReadWordOrZero(v0 + 4u);
                                uint opD64 = 0;
                                try { opD64 = memory.ReadUInt32(0x80327D64u); } catch { }

                                Console.WriteLine(
                                    $"[N64SM64WALK] #{_traceSm64WalkWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} t8=0x{t8:x16} t6=0x{t6:x16} v0=0x{v0:x16} " +
                                    $"[a0]=0x{a0w:x8} [a0+4]=0x{a0w4:x8} [a0+8]=0x{a0w8:x8} [a0+c]=0x{a0wc:x8} " +
                                    $"[t8]=0x{t8w:x8} [t8+4]=0x{t8w4:x8} [t8+8]=0x{t8w8:x8} [t8+c]=0x{t8wc:x8} " +
                                    $"[v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} op@80327d64=0x{opD64:x8}");
                            }

                            if (TraceMegaDispatchWindow
                                && _traceMegaDispatchWindowCount < TraceMegaDispatchWindowLimit
                                && ((pc >= 0x8009FA60u && pc <= 0x8009FD80u)
                                    || (pc >= 0x800A0170u && pc <= 0x800A0310u)))
                            {
                                _traceMegaDispatchWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                uint d0f80 = 0, d0f84 = 0, d0f88 = 0, d0f8c = 0, d0f90 = 0, d0fb8 = 0, cfd88 = 0, cfd90 = 0, cb = 0;
                                try
                                {
                                    d0f80 = memory.ReadUInt32(0x800D0F80u);
                                    d0f84 = memory.ReadUInt32(0x800D0F84u);
                                    d0f88 = memory.ReadUInt32(0x800D0F88u);
                                    d0f8c = memory.ReadUInt32(0x800D0F8Cu);
                                    d0f90 = memory.ReadUInt32(0x800D0F90u);
                                    d0fb8 = memory.ReadUInt32(0x800D0FB8u);
                                    cfd88 = memory.ReadUInt32(0x800CFD88u);
                                    cfd90 = memory.ReadUInt32(0x800CFD90u);
                                    cb = memory.ReadUInt32(0x80204984u);
                                }
                                catch
                                {
                                }

                                Console.WriteLine(
                                    $"[N64MEGA] #{_traceMegaDispatchWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t0=0x{t0:x16} t1=0x{t1:x16} " +
                                    $"d0f80=0x{d0f80:x8} d0f84=0x{d0f84:x8} d0f88=0x{d0f88:x8} d0f8c=0x{d0f8c:x8} " +
                                    $"d0f90=0x{d0f90:x8} d0fb8=0x{d0fb8:x8} cfd88=0x{cfd88:x8} cfd90=0x{cfd90:x8} cb=0x{cb:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceMegaInitWindow
                                && _traceMegaInitWindowCount < TraceMegaInitWindowLimit
                                && pc >= 0x80025C10u
                                && pc <= 0x80025E10u)
                            {
                                ulong t1 = Registers.R4300.Reg[9];
                                bool shouldLog =
                                    pc < 0x80025C24u
                                    ? (_traceMegaInitWindowCount < 24 || t1 <= 0x80u)
                                    : true;

                                if (shouldLog)
                                {
                                    _traceMegaInitWindowCount++;
                                    OpcodeTable.OpcodeDesc megaDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                    int rs = megaDesc.op1;
                                    int rt = megaDesc.op2;
                                    ulong rsValue = Registers.R4300.Reg[rs];
                                    ulong rtValue = Registers.R4300.Reg[rt];
                                    ulong a0 = Registers.R4300.Reg[4];
                                    ulong a1 = Registers.R4300.Reg[5];
                                    ulong a2 = Registers.R4300.Reg[6];
                                    ulong a3 = Registers.R4300.Reg[7];
                                    ulong v0 = Registers.R4300.Reg[2];
                                    ulong v1 = Registers.R4300.Reg[3];
                                    ulong t0 = Registers.R4300.Reg[8];
                                    ulong ra = Registers.R4300.Reg[31];
                                    ulong effAddr = rsValue + (ulong)(int)(short)megaDesc.Imm;
                                    uint rsw = TraceReadWordOrZero(rsValue);
                                    uint rsw4 = TraceReadWordOrZero(rsValue + 4u);
                                    uint effw = TraceReadWordOrZero(effAddr);
                                    uint effw4 = TraceReadWordOrZero(effAddr + 4u);
                                    uint v0w = TraceReadWordOrZero(v0);
                                    uint v0w4 = TraceReadWordOrZero(v0 + 4u);
                                    uint cfd88 = TraceReadWordOrZero(0x800CFD88u);
                                    uint dfd8c = TraceReadWordOrZero(0x800DFD8Cu);
                                    uint cfd90 = TraceReadWordOrZero(0x800CFD90u);
                                    uint d0f90 = TraceReadWordOrZero(0x800D0F90u);
                                    uint d0fb8 = TraceReadWordOrZero(0x800D0FB8u);
                                    uint cb = TraceReadWordOrZero(0x80204984u);
                                    Console.WriteLine(
                                        $"[N64MEGAINIT] #{_traceMegaInitWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                        $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                        $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} v0=0x{v0:x16} v1=0x{v1:x16} " +
                                        $"t0=0x{t0:x16} t1=0x{t1:x16} ra=0x{ra:x16} " +
                                        $"[rs]=0x{rsw:x8} [rs+4]=0x{rsw4:x8} [eff]=0x{effw:x8} [eff+4]=0x{effw4:x8} " +
                                        $"[v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} " +
                                        $"cfd88=0x{cfd88:x8} dfd8c=0x{dfd8c:x8} cfd90=0x{cfd90:x8} d0f90=0x{d0f90:x8} d0fb8=0x{d0fb8:x8} cb=0x{cb:x8} " +
                                        $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                        $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                                }
                            }

                            if (TraceMegaLateWindow
                                && _traceMegaLateWindowCount < TraceMegaLateWindowLimit
                                && ((pc >= 0x80089E80u && pc <= 0x80089EF0u)
                                    || (pc >= 0x80093A00u && pc <= 0x80093B20u)
                                    || (pc >= 0x80092A90u && pc <= 0x80092EC0u)
                                    || (pc >= 0x8009B900u && pc <= 0x8009B980u)
                                    || (pc >= 0x80094440u && pc <= 0x800944C0u)
                                    || (pc >= 0x80092EA0u && pc <= 0x80092EC0u)
                                    || (pc >= 0x800269F0u && pc <= 0x80026A30u)
                                    || (pc >= 0x80027540u && pc <= 0x80027580u)))
                            {
                                _traceMegaLateWindowCount++;
                                OpcodeTable.OpcodeDesc megaLateDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = megaLateDesc.op1;
                                int rt = megaLateDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong effAddr = rsValue + (ulong)(int)(short)megaLateDesc.Imm;
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint rsw4 = TraceReadWordOrZero(rsValue + 4u);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint effw4 = TraceReadWordOrZero(effAddr + 4u);
                                uint v0w = TraceReadWordOrZero(v0);
                                uint v0w4 = TraceReadWordOrZero(v0 + 4u);
                                uint v1w = TraceReadWordOrZero(v1);
                                uint v1w4 = TraceReadWordOrZero(v1 + 4u);
                                uint s0w = TraceReadWordOrZero(s0);
                                uint s0w4 = TraceReadWordOrZero(s0 + 4u);
                                uint s1w = TraceReadWordOrZero(s1);
                                uint s1w4 = TraceReadWordOrZero(s1 + 4u);
                                uint s2w = TraceReadWordOrZero(s2);
                                uint s2w4 = TraceReadWordOrZero(s2 + 4u);
                                uint s3w = TraceReadWordOrZero(s3);
                                uint s3w4 = TraceReadWordOrZero(s3 + 4u);
                                uint cb = TraceReadWordOrZero(0x80204984u);
                                uint late30 = TraceReadWordOrZero(0x80204830u);
                                uint late78 = TraceReadWordOrZero(0x80204978u);
                                uint cb90 = TraceReadWordOrZero(0x800D0F90u);
                                uint cbb8 = TraceReadWordOrZero(0x800D0FB8u);
                                uint cbfd88 = TraceReadWordOrZero(0x800CFD88u);
                                uint cbfd90 = TraceReadWordOrZero(0x800CFD90u);
                                uint lateB0 = TraceReadWordOrZero(0x8020FBB0u);
                                uint lateB4 = TraceReadWordOrZero(0x8020FBB4u);
                                uint piStatus = memory.ReadUInt32(0x04600010u);
                                uint viCurrent = memory.ReadUInt32(0x04400010u);
                                uint piDram = memory.ReadUInt32(0x04600000u);
                                uint piCart = memory.ReadUInt32(0x04600004u);
                                Console.WriteLine(
                                    $"[N64MEGALATE] #{_traceMegaLateWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t0=0x{t0:x16} t1=0x{t1:x16} " +
                                    $"s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [rs+4]=0x{rsw4:x8} [eff]=0x{effw:x8} [eff+4]=0x{effw4:x8} " +
                                    $"[v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} [v1]=0x{v1w:x8} [v1+4]=0x{v1w4:x8} " +
                                    $"[s0]=0x{s0w:x8} [s0+4]=0x{s0w4:x8} [s1]=0x{s1w:x8} [s1+4]=0x{s1w4:x8} " +
                                    $"[s2]=0x{s2w:x8} [s2+4]=0x{s2w4:x8} [s3]=0x{s3w:x8} [s3+4]=0x{s3w4:x8} " +
                                    $"m204830=0x{late30:x8} m204978=0x{late78:x8} d0f90=0x{cb90:x8} d0fb8=0x{cbb8:x8} cfd88=0x{cbfd88:x8} cfd90=0x{cbfd90:x8} " +
                                    $"m20fbb0=0x{lateB0:x8} m20fbb4=0x{lateB4:x8} cb=0x{cb:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"spStatus=0x{memory.ReadUInt32(0x04040010):x8} dpc=0x{memory.ReadUInt32(0x0410000c):x8} " +
                                    $"piStatus=0x{piStatus:x8} viCurrent=0x{viCurrent:x8} piDram=0x{piDram:x8} piCart=0x{piCart:x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceMegaWaitWindow
                                && _traceMegaWaitWindowCount < TraceMegaWaitWindowLimit
                                && (pc >= 0x8008A0D0u && pc <= 0x8008A100u))
                            {
                                _traceMegaWaitWindowCount++;
                                OpcodeTable.OpcodeDesc megaWaitDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = megaWaitDesc.op1;
                                int rt = megaWaitDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong effAddr = rsValue + (ulong)(int)(short)megaWaitDesc.Imm;
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint v0w = TraceReadWordOrZero(v0);
                                uint s0w = TraceReadWordOrZero(s0);
                                uint s1w = TraceReadWordOrZero(s1);
                                uint s2w = TraceReadWordOrZero(s2);
                                uint s3w = TraceReadWordOrZero(s3);
                                Console.WriteLine(
                                    $"[N64MEGAWAIT] #{_traceMegaWaitWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} v0=0x{v0:x16} v1=0x{v1:x16} " +
                                    $"s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [eff]=0x{effw:x8} [v0]=0x{v0w:x8} [s0]=0x{s0w:x8} [s1]=0x{s1w:x8} [s2]=0x{s2w:x8} [s3]=0x{s3w:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"viCurrent=0x{memory.ReadUInt32(0x04400010):x8} piStatus=0x{memory.ReadUInt32(0x04600010):x8} " +
                                    $"piDram=0x{memory.ReadUInt32(0x04600000):x8} piCart=0x{memory.ReadUInt32(0x04600004):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceMegaIdleWindow
                                && _traceMegaIdleWindowCount < TraceMegaIdleWindowLimit
                                && ((pc >= 0x80026A18u && pc <= 0x80026A24u)
                                    || (pc >= 0x8009AAB8u && pc <= 0x8009AAC4u)
                                    || (pc >= 0x800A1690u && pc <= 0x800A16A8u)))
                            {
                                _traceMegaIdleWindowCount++;
                                OpcodeTable.OpcodeDesc idleDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = idleDesc.op1;
                                int rt = idleDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong effAddr = rsValue + (ulong)(int)(short)idleDesc.Imm;
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint v0w = TraceReadWordOrZero(v0);
                                uint v1w = TraceReadWordOrZero(v1);
                                uint idle30 = TraceReadWordOrZero(0x80204830u);
                                uint idle78 = TraceReadWordOrZero(0x80204978u);
                                uint idle84 = TraceReadWordOrZero(0x80204984u);
                                uint idleCfd88 = TraceReadWordOrZero(0x800CFD88u);
                                uint idleCfd90 = TraceReadWordOrZero(0x800CFD90u);
                                uint idle2bf0 = TraceReadWordOrZero(0x80182BF0u);
                                uint idle2bf4 = TraceReadWordOrZero(0x80182BF4u);
                                Common.Logger.PrintWarningLine(
                                    $"[N64MEGAIDLE] #{_traceMegaIdleWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [eff]=0x{effw:x8} [v0]=0x{v0w:x8} [v1]=0x{v1w:x8} " +
                                    $"m204830=0x{idle30:x8} m204978=0x{idle78:x8} m204984=0x{idle84:x8} " +
                                    $"cfd88=0x{idleCfd88:x8} cfd90=0x{idleCfd90:x8} m182bf0=0x{idle2bf0:x8} m182bf4=0x{idle2bf4:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"spStatus=0x{memory.ReadUInt32(0x04040010):x8} spMem=0x{memory.ReadUInt32(0x04040000):x8} spDram=0x{memory.ReadUInt32(0x04040004):x8} " +
                                    $"spRdLen=0x{memory.ReadUInt32(0x04040008):x8} spWrLen=0x{memory.ReadUInt32(0x0404000C):x8} aiLen=0x{memory.ReadUInt32(0x04500004):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} viCurrent=0x{memory.ReadUInt32(0x04400010):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceMegaRspBufferWindow
                                && _traceMegaRspBufferWindowCount < TraceMegaRspBufferWindowLimit
                                && ((pc >= 0x8008EFE0u && pc <= 0x8008F030u)
                                    || (pc >= 0x800908D0u && pc <= 0x80090930u)
                                    || (pc >= 0x80091170u && pc <= 0x80091198u)
                                    || (pc >= 0x80091EA0u && pc <= 0x80091EC8u)))
                            {
                                _traceMegaRspBufferWindowCount++;
                                OpcodeTable.OpcodeDesc rspBufDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = rspBufDesc.op1;
                                int rt = rspBufDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong t4 = Registers.R4300.Reg[12];
                                ulong t5 = Registers.R4300.Reg[13];
                                ulong t6 = Registers.R4300.Reg[14];
                                ulong t7 = Registers.R4300.Reg[15];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong effAddr = rsValue + (ulong)(int)(short)rspBufDesc.Imm;
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint rsw4 = TraceReadWordOrZero(rsValue + 4u);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint effw4 = TraceReadWordOrZero(effAddr + 4u);
                                uint a0w = TraceReadWordOrZero(a0);
                                uint a1w = TraceReadWordOrZero(a1);
                                uint a2w = TraceReadWordOrZero(a2);
                                uint a3w = TraceReadWordOrZero(a3);
                                uint v0w = TraceReadWordOrZero(v0);
                                uint v1w = TraceReadWordOrZero(v1);
                                uint t0w = TraceReadWordOrZero(t0);
                                uint t1w = TraceReadWordOrZero(t1);
                                uint t2w = TraceReadWordOrZero(t2);
                                uint t3w = TraceReadWordOrZero(t3);
                                uint s0w = TraceReadWordOrZero(s0);
                                uint s1w = TraceReadWordOrZero(s1);
                                uint s2w = TraceReadWordOrZero(s2);
                                uint s3w = TraceReadWordOrZero(s3);
                                uint bufB0 = TraceReadWordOrZero(0x802747B0u);
                                uint bufB4 = TraceReadWordOrZero(0x802747B4u);
                                uint bufB8 = TraceReadWordOrZero(0x802747B8u);
                                uint bufBC = TraceReadWordOrZero(0x802747BCu);
                                uint bufD8 = TraceReadWordOrZero(0x802747D8u);
                                uint bufDC = TraceReadWordOrZero(0x802747DCu);
                                Console.WriteLine(
                                    $"[N64MEGARSPBUF] #{_traceMegaRspBufferWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} t4=0x{t4:x16} t5=0x{t5:x16} t6=0x{t6:x16} t7=0x{t7:x16} " +
                                    $"s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [rs+4]=0x{rsw4:x8} [eff]=0x{effw:x8} [eff+4]=0x{effw4:x8} " +
                                    $"[a0]=0x{a0w:x8} [a1]=0x{a1w:x8} [a2]=0x{a2w:x8} [a3]=0x{a3w:x8} " +
                                    $"[v0]=0x{v0w:x8} [v1]=0x{v1w:x8} [t0]=0x{t0w:x8} [t1]=0x{t1w:x8} [t2]=0x{t2w:x8} [t3]=0x{t3w:x8} " +
                                    $"[s0]=0x{s0w:x8} [s1]=0x{s1w:x8} [s2]=0x{s2w:x8} [s3]=0x{s3w:x8} " +
                                    $"bufB0=0x{bufB0:x8} bufB4=0x{bufB4:x8} bufB8=0x{bufB8:x8} bufBC=0x{bufBC:x8} bufD8=0x{bufD8:x8} bufDC=0x{bufDC:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"spStatus=0x{memory.ReadUInt32(0x04040010):x8} spMem=0x{memory.ReadUInt32(0x04040000):x8} spDram=0x{memory.ReadUInt32(0x04040004):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} piDram=0x{memory.ReadUInt32(0x04600000):x8} piCart=0x{memory.ReadUInt32(0x04600004):x8}");
                            }

                            if (TraceMegaFatalWindow
                                && _traceMegaFatalWindowCount < TraceMegaFatalWindowLimit
                                && ((pc >= 0x80089EA0u && pc <= 0x80089EF0u)
                                    || (pc >= 0x80092A90u && pc <= 0x80092EC0u)
                                    || (pc >= 0x80093A20u && pc <= 0x80093B20u)
                                    || (pc >= 0x800269F0u && pc <= 0x80026A30u)))
                            {
                                _traceMegaFatalWindowCount++;
                                OpcodeTable.OpcodeDesc megaFatalDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = megaFatalDesc.op1;
                                int rt = megaFatalDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t6 = Registers.R4300.Reg[14];
                                ulong t7 = Registers.R4300.Reg[15];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong effAddr = rsValue + (ulong)(int)(short)megaFatalDesc.Imm;
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint rsw4 = TraceReadWordOrZero(rsValue + 4u);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint effw4 = TraceReadWordOrZero(effAddr + 4u);
                                uint v0w = TraceReadWordOrZero(v0);
                                uint v0w4 = TraceReadWordOrZero(v0 + 4u);
                                uint late30 = TraceReadWordOrZero(0x80204830u);
                                uint late78 = TraceReadWordOrZero(0x80204978u);
                                uint lateB0 = TraceReadWordOrZero(0x8020FBB0u);
                                uint lateB4 = TraceReadWordOrZero(0x8020FBB4u);
                                uint late2be8 = TraceReadWordOrZero(0x80182BE8u);
                                uint latec3c4 = TraceReadWordOrZero(0x801CC3C4u);
                                uint latec3c8 = TraceReadWordOrZero(0x801CC3C8u);
                                uint cb = TraceReadWordOrZero(0x80204984u);
                                Console.WriteLine(
                                    $"[N64MEGAFATAL] #{_traceMegaFatalWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t0=0x{t0:x16} t1=0x{t1:x16} t6=0x{t6:x16} t7=0x{t7:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [rs+4]=0x{rsw4:x8} [eff]=0x{effw:x8} [eff+4]=0x{effw4:x8} " +
                                    $"[v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} " +
                                    $"m204830=0x{late30:x8} m204978=0x{late78:x8} m20fbb0=0x{lateB0:x8} m20fbb4=0x{lateB4:x8} " +
                                    $"m182be8=0x{late2be8:x8} mc3c4=0x{latec3c4:x8} mc3c8=0x{latec3c8:x8} cb=0x{cb:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"sp=0x{memory.ReadUInt32(0x04040010):x8} dpc=0x{memory.ReadUInt32(0x0410000c):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceMegaStatusCall
                                && _traceMegaStatusCallCount < TraceMegaStatusCallLimit
                                && ((pc >= 0x80089EA0u && pc <= 0x80089EF0u)
                                    || (pc >= 0x80093A20u && pc <= 0x80093B20u)))
                            {
                                _traceMegaStatusCallCount++;
                                OpcodeTable.OpcodeDesc statusDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = statusDesc.op1;
                                int rt = statusDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong ra = Registers.R4300.Reg[31];
                                uint status2be8 = TraceReadWordOrZero(0x80182BE8u);
                                uint statusc3c4 = TraceReadWordOrZero(0x801CC3C4u);
                                uint statusc3c8 = TraceReadWordOrZero(0x801CC3C8u);
                                Common.Logger.PrintWarningLine(
                                    $"[N64MEGASTATUS] #{_traceMegaStatusCallCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} s0=0x{s0:x16} s1=0x{s1:x16} ra=0x{ra:x16} " +
                                    $"m182be8=0x{status2be8:x8} mc3c4=0x{statusc3c4:x8} mc3c8=0x{statusc3c8:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"sp=0x{memory.ReadUInt32(0x04040010):x8} dpc=0x{memory.ReadUInt32(0x0410000c):x8}");
                            }

                            if (TraceMegaPiCallbackWindow
                                && _traceMegaPiCallbackWindowCount < TraceMegaPiCallbackWindowLimit
                                && ((pc >= 0x80025DF0u && pc <= 0x80025E20u)
                                    || (pc >= 0x8008A020u && pc <= 0x8008A0F0u)
                                    || (pc >= 0x80091FD0u && pc <= 0x80092010u)
                                    || (pc >= 0x80092EA8u && pc <= 0x80092EC4u)
                                    || (pc >= 0x8009A460u && pc <= 0x8009A490u)
                                    || (pc >= 0x8009B900u && pc <= 0x8009B980u)))
                            {
                                _traceMegaPiCallbackWindowCount++;
                                OpcodeTable.OpcodeDesc piCbDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = piCbDesc.op1;
                                int rt = piCbDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong effAddr = rsValue + (ulong)(int)(short)piCbDesc.Imm;
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint cb = TraceReadWordOrZero(0x80204984u);
                                uint cb78 = TraceReadWordOrZero(0x80204978u);
                                uint d0f90 = TraceReadWordOrZero(0x800D0F90u);
                                uint d0fb8 = TraceReadWordOrZero(0x800D0FB8u);
                                uint cfd88 = TraceReadWordOrZero(0x800CFD88u);
                                uint cfd90 = TraceReadWordOrZero(0x800CFD90u);
                                Common.Logger.PrintWarningLine(
                                    $"[N64MEGAPICB] #{_traceMegaPiCallbackWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} s0=0x{s0:x16} s1=0x{s1:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [eff]=0x{effw:x8} cb=0x{cb:x8} cb78=0x{cb78:x8} " +
                                    $"d0f90=0x{d0f90:x8} d0fb8=0x{d0fb8:x8} cfd88=0x{cfd88:x8} cfd90=0x{cfd90:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} piDram=0x{memory.ReadUInt32(0x04600000):x8} piCart=0x{memory.ReadUInt32(0x04600004):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
                            }

                            if (TraceSm64QueueWindow
                                && _traceSm64QueueWindowCount < TraceSm64QueueWindowLimit
                                && ((pc >= 0x803227B0 && pc <= 0x80322810)
                                    || (pc >= 0x803274C0 && pc <= 0x80327530)
                                    || (pc >= 0x80322DA0 && pc <= 0x80322F20)))
                            {
                                _traceSm64QueueWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong ra = Registers.R4300.Reg[31];
                                uint q0 = 0, q4 = 0, q8 = 0, qc = 0, qb0 = 0, qb4 = 0;
                                uint a0w = 0, a0w4 = 0, a0wc = 0, a0w10 = 0, a0w14 = 0, a0w18 = 0, a0w1c = 0;
                                q0 = TraceReadWordOrZero(0x803359A0u);
                                q4 = TraceReadWordOrZero(0x803359A4u);
                                q8 = TraceReadWordOrZero(0x803359A8u);
                                qc = TraceReadWordOrZero(0x803359ACu);
                                qb0 = TraceReadWordOrZero(0x803359B0u);
                                qb4 = TraceReadWordOrZero(0x803359B4u);
                                a0w = TraceReadWordOrZero(a0);
                                a0w4 = TraceReadWordOrZero(a0 + 4u);
                                a0wc = TraceReadWordOrZero(a0 + 12u);
                                a0w10 = TraceReadWordOrZero(a0 + 16u);
                                a0w14 = TraceReadWordOrZero(a0 + 20u);
                                a0w18 = TraceReadWordOrZero(a0 + 24u);
                                a0w1c = TraceReadWordOrZero(a0 + 28u);

                                Console.WriteLine(
                                    $"[N64SM64QUEUE] #{_traceSm64QueueWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} v0=0x{v0:x16} v1=0x{v1:x16} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} ra=0x{ra:x16} " +
                                    $"q[a0]=0x{q0:x8} q[a4]=0x{q4:x8} q[a8]=0x{q8:x8} q[ac]=0x{qc:x8} q[b0]=0x{qb0:x8} q[b4]=0x{qb4:x8} " +
                                    $"[a0]=0x{a0w:x8} [a0+4]=0x{a0w4:x8} [a0+c]=0x{a0wc:x8} [a0+10]=0x{a0w10:x8} [a0+14]=0x{a0w14:x8} [a0+18]=0x{a0w18:x8} [a0+1c]=0x{a0w1c:x8}");
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
                                sp38 = TraceReadWordOrZero(sp + 0x38u);
                                sp3c = TraceReadWordOrZero(sp + 0x3Cu);
                                sp40 = TraceReadWordOrZero(sp + 0x40u);
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
                                sp3c = TraceReadWordOrZero(sp + 0x3Cu);
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

                            if (TraceViSwapWindow
                                && _traceViSwapWindowCount < TraceViSwapWindowLimit
                                && ((pc >= 0x80003D20u && pc <= 0x80003FD0u)
                                    || (pc >= 0x80005520u && pc <= 0x800057C0u)))
                            {
                                _traceViSwapWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong t9 = Registers.R4300.Reg[25];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                uint sp24 = TraceReadWordOrZero(sp + 0x24u);
                                uint sp38 = TraceReadWordOrZero(sp + 0x38u);
                                uint sp44 = TraceReadWordOrZero(sp + 0x44u);
                                Console.WriteLine(
                                    $"[N64VISWAP] #{_traceViSwapWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t0=0x{t0:x16} t1=0x{t1:x16} " +
                                    $"t2=0x{t2:x16} t3=0x{t3:x16} t9=0x{t9:x16} s0=0x{s0:x16} " +
                                    $"sp=0x{sp:x16} ra=0x{ra:x16} [sp+24]=0x{sp24:x8} [sp+38]=0x{sp38:x8} [sp+44]=0x{sp44:x8}");
                            }

                            if (TraceViProducerWindow
                                && _traceViProducerWindowCount < TraceViProducerWindowLimit
                                && (pc >= TraceViProducerWindowStart && pc <= TraceViProducerWindowEnd))
                            {
                                _traceViProducerWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong t4 = Registers.R4300.Reg[12];
                                ulong t5 = Registers.R4300.Reg[13];
                                ulong t6 = Registers.R4300.Reg[14];
                                ulong t7 = Registers.R4300.Reg[15];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong s4 = Registers.R4300.Reg[20];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                uint sp20 = TraceReadWordOrZero(sp + 0x20u);
                                uint sp24 = TraceReadWordOrZero(sp + 0x24u);
                                uint sp28 = TraceReadWordOrZero(sp + 0x28u);
                                uint sp2c = TraceReadWordOrZero(sp + 0x2Cu);
                                uint t70 = TraceReadWordOrZero(t7 + 0x0u);
                                uint t74 = TraceReadWordOrZero(t7 + 0x4u);
                                uint t78 = TraceReadWordOrZero(t7 + 0x8u);
                                uint t7c = TraceReadWordOrZero(t7 + 0xCu);
                                Console.WriteLine(
                                    $"[N64VIPROD] #{_traceViProducerWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t0=0x{t0:x16} t1=0x{t1:x16} " +
                                    $"t2=0x{t2:x16} t3=0x{t3:x16} t4=0x{t4:x16} t5=0x{t5:x16} " +
                                    $"t6=0x{t6:x16} t7=0x{t7:x16} s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} s4=0x{s4:x16} " +
                                    $"sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[sp+20]=0x{sp20:x8} [sp+24]=0x{sp24:x8} [sp+28]=0x{sp28:x8} [sp+2c]=0x{sp2c:x8} " +
                                    $"[t7+0]=0x{t70:x8} [t7+4]=0x{t74:x8} [t7+8]=0x{t78:x8} [t7+c]=0x{t7c:x8}");
                            }

                            if (TracePcWindow
                                && _tracePcWindowCount < TracePcWindowLimit
                                && pc >= TracePcWindowStart
                                && pc <= TracePcWindowEnd)
                            {
                                _tracePcWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t5 = Registers.R4300.Reg[13];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s4 = Registers.R4300.Reg[20];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                ulong cop0Status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
                                ulong cop0Cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
                                uint miIntr = TraceReadWordOrZero(0xA4300008u);
                                uint miMask = TraceReadWordOrZero(0xA430000Cu);
                                uint viCurrent = TraceReadWordOrZero(0xA4400010u);
                                Console.WriteLine(
                                    $"[N64PCWIN] #{_tracePcWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} " +
                                    $"v0=0x{v0:x16} v1=0x{v1:x16} t5=0x{t5:x16} s0=0x{s0:x16} s1=0x{s1:x16} s4=0x{s4:x16} " +
                                    $"sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"cop0Status=0x{cop0Status:x8} cop0Cause=0x{cop0Cause:x8} " +
                                    $"miIntr=0x{miIntr:x8} miMask=0x{miMask:x8} viCurrent=0x{viCurrent:x8}");
                            }

                            if (TraceMegaLowRamWindow
                                && _traceMegaLowRamWindowCount < TraceMegaLowRamWindowLimit
                                && pc >= 0x800A1680u
                                && pc <= 0x800A16F0u)
                            {
                                _traceMegaLowRamWindowCount++;
                                OpcodeTable.OpcodeDesc lowRamDesc = new OpcodeTable.OpcodeDesc(Opcode);
                                int rs = lowRamDesc.op1;
                                int rt = lowRamDesc.op2;
                                ulong rsValue = Registers.R4300.Reg[rs];
                                ulong rtValue = Registers.R4300.Reg[rt];
                                short imm = (short)lowRamDesc.Imm;
                                ulong effAddr = rsValue + (ulong)(long)imm;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong a3 = Registers.R4300.Reg[7];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong sp = Registers.R4300.Reg[29];
                                ulong ra = Registers.R4300.Reg[31];
                                uint rsw = TraceReadWordOrZero(rsValue);
                                uint rsw4 = TraceReadWordOrZero(rsValue + 4u);
                                uint effw = TraceReadWordOrZero(effAddr);
                                uint effw4 = TraceReadWordOrZero(effAddr + 4u);
                                uint low100 = TraceReadWordOrZero(0x80000100u);
                                uint low180 = TraceReadWordOrZero(0x80000180u);
                                uint low184 = TraceReadWordOrZero(0x80000184u);
                                uint low300 = TraceReadWordOrZero(0x80000300u);
                                uint cb = TraceReadWordOrZero(0x80204984u);
                                Console.WriteLine(
                                    $"[N64MEGALOWRAM] #{_traceMegaLowRamWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} imm={imm} eff=0x{effAddr:x16} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} v0=0x{v0:x16} v1=0x{v1:x16} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} " +
                                    $"s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                                    $"[rs]=0x{rsw:x8} [rs+4]=0x{rsw4:x8} [eff]=0x{effw:x8} [eff+4]=0x{effw4:x8} " +
                                    $"m100=0x{low100:x8} m180=0x{low180:x8} m184=0x{low184:x8} m300=0x{low300:x8} cb=0x{cb:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"spStatus=0x{memory.ReadUInt32(0x04040010):x8} dpc=0x{memory.ReadUInt32(0x0410000c):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} viCurrent=0x{memory.ReadUInt32(0x04400010):x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
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

        private static void ApplyBootRomHleStartup(uint romType, uint resetType, uint tvType, uint cicSeed)
        {
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = 0x34000000;
            Registers.COP0.Reg[Registers.COP0.CONFIG_REG] = 0x7006E463;
            // The HLE fast path skips portions of the real boot ROM that would normally
            // scrub transient exception state before IPL3 runs. Leaving power-on values
            // like Cause=0x5c and EPC/BadVAddr=-1 visible here can send some games into
            // the low-RAM fatal loop immediately during early boot.
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = 0x00000000;
            Registers.COP0.Reg[Registers.COP0.EPC_REG] = 0x00000000;
            Registers.COP0.Reg[Registers.COP0.BADVADDR_REG] = 0x00000000;
            Registers.COP0.Reg[Registers.COP0.ERROREPC_REG] = 0x00000000;

            // Seed the minimal boot HLE startup subset:
            // stop SP/PI, blank VI, mute AI, and seed IPL3 execution state.
            memory.WriteUInt32(0xA4040010u, 0x0000000Au); // SP_STATUS: halt + clear intr
            memory.WriteUInt32(0xA4600010u, 0x00000003u); // PI_STATUS: reset + clear intr

            memory.WriteUInt32(0xA440000Cu, 1023u); // VI_INTR
            memory.WriteUInt32(0xA4400010u, 0u);    // VI_CURRENT
            memory.WriteUInt32(0xA4400024u, 0u);    // VI_H_START

            memory.WriteUInt32(0xA4500000u, 0u);    // AI_DRAM_ADDR
            memory.WriteUInt32(0xA4500004u, 0u);    // AI_LEN

            uint pif24 = memory.ReadUInt32(0xBFC007E4u);
            uint s7 = (pif24 >> 18) & 0x1u;
            uint seed = (pif24 >> 8) & 0xFFu;
            Registers.R4300.Reg[19] = romType;
            Registers.R4300.Reg[20] = tvType;
            Registers.R4300.Reg[21] = resetType;
            Registers.R4300.Reg[22] = seed;
            Registers.R4300.Reg[23] = s7;

            uint bsdDom1Config = memory.ReadUInt32(0xB0000000u);
            memory.WriteUInt32(0xA4600014u, bsdDom1Config & 0xFFu);
            memory.WriteUInt32(0xA4600018u, (bsdDom1Config >> 8) & 0xFFu);
            memory.WriteUInt32(0xA460001Cu, (bsdDom1Config >> 16) & 0x0Fu);
            memory.WriteUInt32(0xA4600020u, (bsdDom1Config >> 20) & 0x03u);

            memory.FastMemoryCopy(0xA4000040u, 0xB0000040u, 0xFC0);
            memory.WriteUInt32(0xA4001000u, 0x3C0DBFC0u);
            memory.WriteUInt32(0xA4001004u, 0x8DA807FCu);
            memory.WriteUInt32(0xA4001008u, 0x25AD07C0u);
            memory.WriteUInt32(0xA400100Cu, 0x31080080u);
            memory.WriteUInt32(0xA4001010u, 0x5500FFFCu);
            memory.WriteUInt32(0xA4001014u, 0x3C0DBFC0u);
            memory.WriteUInt32(0xA4001018u, 0x8DA80024u);
            memory.WriteUInt32(0xA400101Cu, 0x3C0BB000u);

            Registers.R4300.Reg[11] = 0xFFFFFFFFA4000040;
            Registers.R4300.Reg[29] = 0xFFFFFFFFA4001FF0;
            Registers.R4300.Reg[31] = 0xFFFFFFFFA4001550;
            Registers.R4300.PC = 0xA4000040;
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

        public static void SaveState(BinaryWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (memory == null)
                throw new InvalidOperationException("N64 memory is not initialized.");

            const int version = 1;
            writer.Write(version);
            writer.Write(CycleCounter);
            writer.Write(Count);
            writer.Write(UnknownOpcodeCount);
            writer.Write(_executingDelaySlot);
            writer.Write(_delaySlotBranchPc);
            writer.Write(_delaySlotExceptionPending);
            writer.Write(_delaySlotExceptionBranchPc);
            writer.Write(_loadLinkedActive);
            writer.Write(Registers.R4300.PC);
            writer.Write(Registers.R4300.HI);
            writer.Write(Registers.R4300.LO);
            WriteUlongArray(writer, Registers.R4300.Reg);
            WriteUlongArray(writer, Registers.COP0.Reg);
            WriteUlongArray(writer, Registers.COP1.Reg);
            WriteUintArray(writer, Registers.COP1.Control);
            writer.Write(COP0.COP0_ON);
            writer.Write(COP1.COP1_ON);
            TLB.SaveState(writer);
            memory.SaveState(writer);
        }

        public static void LoadState(BinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (memory == null)
                throw new InvalidOperationException("N64 memory is not initialized.");

            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported N64 CPU savestate version: {version}.");

            CycleCounter = reader.ReadUInt64();
            Count = reader.ReadUInt64();
            UnknownOpcodeCount = reader.ReadInt64();
            _executingDelaySlot = reader.ReadBoolean();
            _delaySlotBranchPc = reader.ReadUInt32();
            _delaySlotExceptionPending = reader.ReadBoolean();
            _delaySlotExceptionBranchPc = reader.ReadUInt32();
            _loadLinkedActive = reader.ReadBoolean();
            Registers.R4300.PC = reader.ReadUInt32();
            Registers.R4300.HI = reader.ReadUInt64();
            Registers.R4300.LO = reader.ReadUInt64();
            ReadUlongArray(reader, Registers.R4300.Reg);
            ReadUlongArray(reader, Registers.COP0.Reg);
            ReadUlongArray(reader, Registers.COP1.Reg);
            ReadUintArray(reader, Registers.COP1.Control);
            COP0.COP0_ON = reader.ReadBoolean();
            COP1.COP1_ON = reader.ReadBoolean();
            TLB.LoadState(reader);
            memory.LoadState(reader);

            lock (UnknownOpcodeLock)
            {
                UnknownOpcodeByPc.Clear();
                UnknownOpcodeByValue.Clear();
            }
            lock (HotPcSamplesLock)
            {
                HotPcSamples.Clear();
                _hotPcInstructionCounter = 0;
            }

            Common.Measure.CycleCounter = CycleCounter;
        }

        private static void WriteUlongArray(BinaryWriter writer, ulong[] values)
        {
            writer.Write(values.Length);
            for (int i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        private static void ReadUlongArray(BinaryReader reader, ulong[] values)
        {
            int length = reader.ReadInt32();
            if (length != values.Length)
                throw new InvalidDataException($"Unsupported N64 ulong array length: {length}.");
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadUInt64();
        }

        private static void WriteUintArray(BinaryWriter writer, uint[] values)
        {
            writer.Write(values.Length);
            for (int i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        private static void ReadUintArray(BinaryReader reader, uint[] values)
        {
            int length = reader.ReadInt32();
            if (length != values.Length)
                throw new InvalidDataException($"Unsupported N64 uint array length: {length}.");
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadUInt32();
        }
    }
}
