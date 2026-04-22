using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryu64.MIPS
{
    public class Memory
    {
        private delegate void MemoryEvent();
        private static readonly bool StrictDataTlb =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_LOOSE_DATA_TLB"), "1", StringComparison.Ordinal);
        private static readonly bool AllowDirectLowPhysicalWindow =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_STRICT_LOWSEG"), "1", StringComparison.Ordinal);
        private static readonly bool AllowLowPhysicalFallbackOnTlbMiss =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_LOWSEG_MISS_FALLBACK"), "0", StringComparison.Ordinal);
        private static readonly bool AllowNullPageFallbackOnTlbMiss =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_LOWSEG_NULLPAGE_FALLBACK"), "1", StringComparison.Ordinal);
        private static readonly bool TraceN64Io =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_IO"), "1", StringComparison.Ordinal);
        private static readonly bool TraceRspTaskDmem =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RSP_TASK_DMEM"), "1", StringComparison.Ordinal);
        private static readonly bool TracePiInterruptLifecycle =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_IRQ"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSiInterruptLifecycle =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SI_IRQ"), "1", StringComparison.Ordinal);
        private static readonly bool TraceViInterruptLifecycle =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_IRQ"), "1", StringComparison.Ordinal);
        private static readonly bool TraceViRegisterLifecycle =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_REGS"), "1", StringComparison.Ordinal);
        private static readonly uint? TraceWatchAddress = ParseOptionalHexEnv("EUTHERDRIVE_TRACE_N64_WATCH_ADDR");
        private static readonly uint? TraceWatchRangeStart = ParseOptionalHexEnv("EUTHERDRIVE_TRACE_N64_WATCH_RANGE_START");
        private static readonly uint? TraceWatchRangeEnd = ParseOptionalHexEnv("EUTHERDRIVE_TRACE_N64_WATCH_RANGE_END");
        private static readonly bool TraceMegaCallbackBlock =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_CALLBACKS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceMegaFatalBlock =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_FATAL_BLOCK"), "1", StringComparison.Ordinal);
        private static readonly bool TraceMegaStatusBlock =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_STATUS_CALL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceExceptionVectorWrites =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_EXCEPTION_VECTOR_WRITES"), "0", StringComparison.Ordinal);
        private static readonly bool TraceLowRamMutationWrites =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_LOW_RAM_MUTATIONS"), "0", StringComparison.Ordinal);
        private static readonly bool MirrorPiRdLenAsCartToDram =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_PI_RDLEN_MIRROR"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSm64SlotWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SM64_SLOT_WRITES"), "1", StringComparison.Ordinal);
        private static readonly bool AutoCompleteRspTaskOnHaltClear =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_SP_AUTOCOMPLETE"), "1", StringComparison.Ordinal);
        private static readonly bool EnableRspTaskHleDispatcher =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_DISABLE_RSP_TASK_HLE"), "1", StringComparison.Ordinal);
        private static readonly bool EnableRspInterpreter =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_INTERPRETER"), "0", StringComparison.Ordinal);
        private static readonly bool EnableRspInterpreterGraphicsOnly =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_INTERPRETER_GRAPHICS_ONLY"), "1", StringComparison.Ordinal);
        private static ulong _rspKickCount;
        private static int _traceWatchRangeLogCount;
        private static int _traceExceptionVectorWriteCount;
        private static int _traceLowRamMutationWriteCount;
        private static int _traceRspDmaWindowLogCount;
        private const int TraceWatchRangeLogLimit = 512;
        private const int TraceExceptionVectorWriteLimit = 512;
        private const int TraceLowRamMutationWriteLimit = 1024;
        private const int TraceRspDmaWindowLogLimit = 128;
        private static bool _warnedRspTaskStub;
        private const uint SpStatusHalt = 0x00000001u;
        private const uint SpStatusBroke = 0x00000002u;
        private const uint SpStatusDmaBusy = 0x00000004u;
        private const uint SpStatusIntrBreak = 0x00000040u;
        private const uint SpStatusTaskDone = 0x00000200u;
        private const uint DpcStatusXbusDmemDma = 0x00000001u;
        private const uint DpcStatusFreeze = 0x00000002u;
        private const uint DpcStatusFlush = 0x00000004u;
        private const uint DpcStatusStartGclk = 0x00000008u;
        private const uint DpcStatusCbufReady = 0x00000080u;
        private const uint DpcStatusEndValid = 0x00000200u;
        private const uint DpcStatusStartValid = 0x00000400u;
        private const uint DpcClrXbusDmemDma = 0x00000001u;
        private const uint DpcSetXbusDmemDma = 0x00000002u;
        private const uint DpcClrFreeze = 0x00000004u;
        private const uint DpcSetFreeze = 0x00000008u;
        private const uint DpcClrFlush = 0x00000010u;
        private const uint DpcSetFlush = 0x00000020u;
        private const uint PiStatusDmaBusy = 0x00000001u;
        private const uint PiStatusIoBusy = 0x00000002u;
        private const uint PiStatusError = 0x00000004u;
        private const uint PiStatusInterrupt = 0x00000008u;
        private const uint PiDmaCyclesMinimum = 0x00001000u;

        private static uint? ParseOptionalHexEnv(string name)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);

            if (uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint parsed))
                return parsed;

            return null;
        }

        private static bool TryGetNormalizedWatchRange(out uint start, out uint end)
        {
            if (!TraceWatchRangeStart.HasValue || !TraceWatchRangeEnd.HasValue)
            {
                start = 0;
                end = 0;
                return false;
            }

            start = TraceWatchRangeStart.Value;
            end = TraceWatchRangeEnd.Value;
            if (start > end)
            {
                uint tmp = start;
                start = end;
                end = tmp;
            }

            return true;
        }

        private static bool RangeOverlaps(uint address, uint size, uint start, uint end)
        {
            uint last = address + Math.Max(1u, size) - 1u;
            return address <= end && last >= start;
        }

        private static bool ShouldTraceWatchRange(uint virtualAddress, uint physicalAddress, uint size)
        {
            if (!TryGetNormalizedWatchRange(out uint start, out uint end))
                return false;

            return RangeOverlaps(virtualAddress, size, start, end)
                || RangeOverlaps(physicalAddress, size, start, end);
        }

        private static bool ShouldTraceWatchRange(uint virtualAddress, uint physicalAddress)
        {
            return ShouldTraceWatchRange(virtualAddress, physicalAddress, 4);
        }

        private static void TraceWatchRangeAccess(string op, uint virtualAddress, uint physicalAddress, uint value)
        {
            if (_traceWatchRangeLogCount >= TraceWatchRangeLogLimit)
                return;

            _traceWatchRangeLogCount++;
            Common.Logger.PrintWarningLine(
                $"[N64WATCH] {op} addr=0x{virtualAddress:x8} phys=0x{physicalAddress:x8} value=0x{value:x8} pc=0x{Registers.R4300.PC:x8}");
        }

        private void TraceWatchRangeWrite(string op, uint virtualAddress, uint physicalAddress, uint value)
        {
            if (_traceWatchRangeLogCount >= TraceWatchRangeLogLimit)
                return;

            _traceWatchRangeLogCount++;
            Common.Logger.PrintWarningLine(
                $"[N64WATCH] {op} addr=0x{virtualAddress:x8} phys=0x{physicalAddress:x8} value=0x{value:x8} " +
                $"pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        private static bool IsExceptionVectorPhysicalAddress(uint physicalAddress, uint size)
        {
            uint start = physicalAddress;
            uint end = physicalAddress + Math.Max(1u, size) - 1u;
            return start <= 0x000001BFu && end >= 0x00000100u;
        }

        private static void TraceExceptionVectorWrite(string op, uint virtualAddress, uint physicalAddress, ulong oldValue, ulong newValue, uint size)
        {
            if (!TraceExceptionVectorWrites || !IsExceptionVectorPhysicalAddress(physicalAddress, size))
                return;

            if (_traceExceptionVectorWriteCount >= TraceExceptionVectorWriteLimit)
                return;

            _traceExceptionVectorWriteCount++;
            Common.Logger.PrintWarningLine(
                $"[N64EXCVEC] {op} addr=0x{virtualAddress:x8} phys=0x{physicalAddress:x8} size={size} " +
                $"old=0x{oldValue:x16} new=0x{newValue:x16} pc=0x{Registers.R4300.PC:x8}");
        }

        private static bool IsLowRamDiagnosticPhysicalAddress(uint physicalAddress, uint size)
        {
            return RangeOverlaps(physicalAddress, size, 0x00000100u, 0x000003FFu);
        }

        private static bool ShouldTraceLowRamMutation(uint physicalAddress, uint size, ulong oldValue, ulong newValue)
        {
            if (!TraceLowRamMutationWrites || !IsLowRamDiagnosticPhysicalAddress(physicalAddress, size) || oldValue == newValue)
                return false;

            bool clearsInstalledLowRamWord = oldValue != 0 && newValue == 0;
            bool touchesLowRamTrapWord = RangeOverlaps(physicalAddress, size, 0x00000300u, 0x00000303u);
            return clearsInstalledLowRamWord || touchesLowRamTrapWord;
        }

        private void TraceLowRamMutationWrite(string op, uint virtualAddress, uint physicalAddress, ulong oldValue, ulong newValue, uint size)
        {
            if (!ShouldTraceLowRamMutation(physicalAddress, size, oldValue, newValue))
                return;

            if (_traceLowRamMutationWriteCount >= TraceLowRamMutationWriteLimit)
                return;

            _traceLowRamMutationWriteCount++;
            Common.Logger.PrintWarningLine(
                $"[N64LOWRAMMUT] {op} addr=0x{virtualAddress:x8} phys=0x{physicalAddress:x8} size={size} " +
                $"old=0x{oldValue:x16} new=0x{newValue:x16} {BuildLowRamStoreContext(virtualAddress, physicalAddress, size)}");
        }

        private static bool IsTraceN64PiDmaEnabled()
        {
            return string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal);
        }

        private static bool IsTraceN64SpDmaEnabled()
        {
            return IsTraceN64PiDmaEnabled()
                || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SP_DMA"), "1", StringComparison.Ordinal);
        }

        private static bool IsTraceN64SpMmioEnabled()
        {
            return IsTraceN64SpDmaEnabled()
                || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SP_MMIO"), "1", StringComparison.Ordinal);
        }

        private static bool ShouldTraceSpRegisterStore(uint physicalAddress)
        {
            if (!IsTraceN64SpMmioEnabled())
                return false;

            return physicalAddress >= 0x04040000u && physicalAddress <= 0x0404000Fu;
        }

        private static string DescribeSpRegisterStore(uint physicalAddress)
        {
            uint wordBase = physicalAddress & 0xFFFFFFFCu;
            string registerName;
            switch (wordBase)
            {
                case 0x04040000u:
                    registerName = "SP_MEM_ADDR";
                    break;
                case 0x04040004u:
                    registerName = "SP_DRAM_ADDR";
                    break;
                case 0x04040008u:
                    registerName = "SP_RD_LEN";
                    break;
                case 0x0404000Cu:
                    registerName = "SP_WR_LEN";
                    break;
                default:
                    registerName = "SP_REG";
                    break;
            }

            return $"{registerName}[+0x{physicalAddress - wordBase:x}]";
        }

        private string BuildLowRamStoreContext(uint virtualAddress, uint physicalAddress, uint size)
        {
            uint opcode = 0;
            try { opcode = ReadUInt32(Registers.R4300.PC); } catch { }

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

            uint a0w = 0, a1w = 0, a2w = 0, a3w = 0, v0w = 0, s0w = 0, s1w = 0, s2w = 0, s3w = 0;
            try { a0w = ReadUInt32((uint)a0); } catch { }
            try { a1w = ReadUInt32((uint)a1); } catch { }
            try { a2w = ReadUInt32((uint)a2); } catch { }
            try { a3w = ReadUInt32((uint)a3); } catch { }
            try { v0w = ReadUInt32((uint)v0); } catch { }
            try { s0w = ReadUInt32((uint)s0); } catch { }
            try { s1w = ReadUInt32((uint)s1); } catch { }
            try { s2w = ReadUInt32((uint)s2); } catch { }
            try { s3w = ReadUInt32((uint)s3); } catch { }

            OpcodeTable.OpcodeDesc desc = new OpcodeTable.OpcodeDesc(opcode);
            int rs = desc.op1;
            int rt = desc.op2;
            short imm = (short)desc.Imm;
            ulong rsValue = Registers.R4300.Reg[rs];
            ulong rtValue = Registers.R4300.Reg[rt];
            ulong effAddr = rsValue + (ulong)(long)imm;
            uint effw = 0;
            uint effw4 = 0;
            try { effw = ReadUInt32((uint)effAddr); } catch { }
            try { effw4 = ReadUInt32((uint)effAddr + 4u); } catch { }

            return
                $"pc=0x{Registers.R4300.PC:x8} op=0x{opcode:x8} vaddr=0x{virtualAddress:x8} phys=0x{physicalAddress:x8} size={size} " +
                $"rs=r{rs}=0x{rsValue:x16} rt=r{rt}=0x{rtValue:x16} imm={imm} eff=0x{effAddr:x16} [eff]=0x{effw:x8} [eff+4]=0x{effw4:x8} " +
                $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} v0=0x{v0:x16} v1=0x{v1:x16} " +
                $"t0=0x{t0:x16} t1=0x{t1:x16} s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} sp=0x{sp:x16} ra=0x{ra:x16} " +
                $"[a0]=0x{a0w:x8} [a1]=0x{a1w:x8} [a2]=0x{a2w:x8} [a3]=0x{a3w:x8} [v0]=0x{v0w:x8} " +
                $"[s0]=0x{s0w:x8} [s1]=0x{s1w:x8} [s2]=0x{s2w:x8} [s3]=0x{s3w:x8} " +
                $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                $"origin={_writeUInt8Origin ?? "direct"}";
        }

        private void WithWriteUInt8Origin(string origin, Action action)
        {
            string previous = _writeUInt8Origin;
            _writeUInt8Origin = origin;
            try
            {
                action();
            }
            finally
            {
                _writeUInt8Origin = previous;
            }
        }

        private static void RefreshCpuInterruptView()
        {
            R4300.RefreshRcpInterruptPending();
        }

        private static void RaiseCpuInterruptFromRcpEvent()
        {
            R4300.RefreshRcpInterruptPending();
        }

        public readonly byte[] SP_DMEM_RW         = new byte[0x1000];
        public readonly byte[] SP_IMEM_RW         = new byte[0x1000];
        public readonly byte[] SP_MEM_ADDR_REG_RW = new byte[4];
        public readonly byte[] SP_DRAM_ADDR_REG_RW = new byte[4];
        public readonly byte[] SP_RD_LEN_REG_RW   = new byte[4];
        public readonly byte[] SP_WR_LEN_REG_RW   = new byte[4];
        public readonly byte[] SP_STATUS_REG_R    = new byte[4];
        public readonly byte[] SP_STATUS_REG_W    = new byte[4];
        public readonly byte[] SP_DMA_FULL_REG_R  = new byte[4];
        public readonly byte[] SP_DMA_FULL_REG_W  = new byte[4];
        public readonly byte[] SP_DMA_BUSY_REG_R  = new byte[4];
        public readonly byte[] SP_DMA_BUSY_REG_W  = new byte[4];
        public readonly byte[] SP_SEMAPHORE_REG_R = new byte[4];
        public readonly byte[] SP_SEMAPHORE_REG_W = new byte[4];
        public readonly byte[] SP_PC_REG_RW       = new byte[4];
        public readonly byte[] SP_IBIST_REG_RW    = new byte[4];

        public readonly byte[] DPC_START_REG_RW    = new byte[4];
        public readonly byte[] DPC_END_REG_RW      = new byte[4];
        public readonly byte[] DPC_CURRENT_REG_RW  = new byte[4];
        public readonly byte[] DPC_STATUS_REG_R    = new byte[4];
        public readonly byte[] DPC_STATUS_REG_W    = new byte[4];
        public readonly byte[] DPC_CLOCK_REG_RW    = new byte[4];
        public readonly byte[] DPC_BUFBUSY_REG_RW  = new byte[4];
        public readonly byte[] DPC_PIPEBUSY_REG_RW = new byte[4];
        public readonly byte[] DPC_TMEM_REG_RW     = new byte[4];
        public readonly byte[] DPS_TBIST_REG_RW        = new byte[4];
        public readonly byte[] DPS_TEST_MODE_REG_RW    = new byte[4];
        public readonly byte[] DPS_BUFTEST_ADDR_REG_RW = new byte[4];
        public readonly byte[] DPS_BUFTEST_DATA_REG_RW = new byte[4];

        public readonly byte[] MI_INIT_MODE_REG_R = new byte[4];
        public readonly byte[] MI_INIT_MODE_REG_W = new byte[4];
        public readonly byte[] MI_VERSION_REG_RW  = new byte[4];
        public readonly byte[] MI_INTR_REG_R      = new byte[4];
        public readonly byte[] MI_INTR_MASK_REG_R = new byte[4];
        public readonly byte[] MI_INTR_MASK_REG_W = new byte[4];

        public readonly byte[] VI_STATUS_REG_RW  = new byte[4];
        public readonly byte[] VI_ORIGIN_REG_RW  = new byte[4];
        public readonly byte[] VI_WIDTH_REG_RW   = new byte[4];
        public readonly byte[] VI_INTR_REG_RW    = new byte[4];
        public readonly byte[] VI_CURRENT_REG_RW = new byte[4];
        public readonly byte[] VI_BURST_REG_RW   = new byte[4];
        public readonly byte[] VI_V_SYNC_REG_RW  = new byte[4];
        public readonly byte[] VI_H_SYNC_REG_RW  = new byte[4];
        public readonly byte[] VI_LEAP_REG_RW    = new byte[4];
        public readonly byte[] VI_H_START_REG_RW = new byte[4];
        public readonly byte[] VI_V_START_REG_RW = new byte[4];
        public readonly byte[] VI_V_BURST_REG_RW = new byte[4];
        public readonly byte[] VI_X_SCALE_REG_RW = new byte[4];
        public readonly byte[] VI_Y_SCALE_REG_RW = new byte[4];

        public readonly byte[] AI_DRAM_ADDR_REG_W = new byte[4];
        public readonly byte[] AI_LEN_REG_RW      = new byte[4];
        public readonly byte[] AI_CONTROL_REG_W   = new byte[4];
        public readonly byte[] AI_STATUS_REG_R    = new byte[4];
        public readonly byte[] AI_STATUS_REG_W    = new byte[4];
        public readonly byte[] AI_DACRATE_REG_W   = new byte[4];
        public readonly byte[] AI_BITRATE_REG_W   = new byte[4];

        public readonly byte[] PI_DRAM_ADDR_REG_RW    = new byte[4];
        public readonly byte[] PI_CART_ADDR_REG_RW    = new byte[4];
        public readonly byte[] PI_RD_LEN_REG_RW       = new byte[4];
        public readonly byte[] PI_WR_LEN_REG_RW       = new byte[4];
        public readonly byte[] PI_STATUS_REG_R        = new byte[4];
        public readonly byte[] PI_STATUS_REG_W        = new byte[4];
        public readonly byte[] PI_BSD_DOM1_LAT_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM1_PWD_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM1_PGS_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM1_RLS_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_LAT_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_PWD_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_PGS_REG_RW = new byte[4];
        public readonly byte[] PI_BSD_DOM2_RLS_REG_RW = new byte[4];

        public readonly byte[] SI_DRAM_ADDR_REG_RW      = new byte[4];
        public readonly byte[] SI_PIF_ADDR_RD64B_REG_RW = new byte[4];
        public readonly byte[] SI_PIF_ADDR_WR64B_REG_RW = new byte[4];
        public readonly byte[] SI_STATUS_REG_R          = new byte[4];
        public readonly byte[] SI_STATUS_REG_W          = new byte[4];
        private readonly byte[] SI_MIRROR_RAM           = new byte[0x20000];

        public readonly byte[] RI_MODE_REG_RW         = new byte[4];
        public readonly byte[] RI_CONFIG_REG_RW       = new byte[4];
        public readonly byte[] RI_CURRENT_LOAD_REG_RW = new byte[4];
        public readonly byte[] RI_SELECT_REG_RW       = new byte[4];
        public readonly byte[] RI_REFRESH_REG_RW      = new byte[4];
        public readonly byte[] RI_LATENCY_REG_RW      = new byte[4];
        public readonly byte[] RI_ERROR_REG_RW        = new byte[4];
        public readonly byte[] RI_WERROR_REG_RW       = new byte[4];

        public readonly byte[] RDRAM     = new byte[8388608];
        public readonly byte[] RDRAMReg  = new byte[1048576];
        public readonly byte[] PIFROM    = new byte[1984];
        public readonly byte[] PIFRAM    = new byte[64];
        private readonly byte[] OpenBus  = new byte[4];
        private readonly byte[] _rom;
        private uint _openBusMissCount;
        private bool _piDmaBusy;
        private bool _piInterruptDelayArmed;
        private uint _piInterruptDelayRemaining;
        private uint _piIrqRaiseCount;
        private uint _piIrqClearCount;
        private bool _siDmaActive;
        private bool _siDmaReadToDram;
        private uint _siDramAddr;
        private bool _siDirectPifWriteActive;
        private bool _siInterruptDelayArmed;
        private uint _siInterruptDelayRemaining;
        private uint _aiFifo0Address;
        private uint _aiFifo0Length;
        private uint _aiFifo0Duration;
        private uint _aiFifo1Address;
        private uint _aiFifo1Length;
        private uint _aiFifo1Duration;
        private bool _aiInterruptDelayArmed;
        private uint _aiInterruptDelayRemaining;
        private bool _aiDelayedCarry;
        private uint _viCurrentLine;
        private uint _viLineCycleAccum;
        private uint _viFrameDelayCycles;
        private uint _viInterruptCyclesRemaining;
        private uint _viField;
        private uint _rdramWriteEpoch;
        private uint _lastViOriginWriteValue;
        private uint _lastViOriginWritePc;
        private uint _lastPlausibleViOriginWriteValue;
        private uint _lastPlausibleViOriginWritePc;
        private bool _warnedRspTaskHle;
        private bool _warnedRspInterpreterFallback;
        private bool _spDmaBusy;
        private bool _spDmaFull;
        private bool _spDmaDelayArmed;
        private uint _spDmaDelayRemaining;
        private SpDmaRequest _spQueuedDma;
        private bool _spQueuedDmaValid;
        private bool _rspTaskActive;
        private bool _rspTaskDispatching;
        private uint _rspTaskCyclesRemaining;
        private uint _rspInterruptDelayRemaining;
        private bool _rspInterruptDelayArmed;
        private bool _rspTaskLocked;
        private uint _activeRspTracePc;
        private bool _hasActiveRspTracePc;
        private string _writeUInt8Origin;
        private uint _dpInterruptDelayRemaining;
        private bool _dpInterruptDelayArmed;
        private RspTask _activeRspTask;
        private readonly RspInterpreter _rspInterpreter;

        private const uint CpuCyclesPerViFrame = 1_562_500; // 93.75 MHz / 60 Hz
        private const uint CpuCyclesPerSecond = CpuCyclesPerViFrame * 60u;
        private const uint DefaultViLinesPerFrame = 1024;
        private const uint DefaultCpuCyclesPerViLine = CpuCyclesPerViFrame / DefaultViLinesPerFrame;
        private const uint PlausibleFramebufferOriginFloor = 0x00001000u;
        private const uint RdramPageSize = 0x1000u;
        private const int RdramPageCount = (8 * 1024 * 1024) / 0x1000;
        private const uint SiStatusDmaBusy = 0x00000001u;
        private const uint SiStatusIoBusy = 0x00000002u;
        private const uint SiStatusInterrupt = 0x00001000u;
        private const uint SiDmaDurationCycles = 0x900;
        private const uint AiStatusBusy = 0x40000000u;
        private const uint AiStatusFull = 0x80000000u;

        private List<MemEntry> MemoryMapList = new List<MemEntry>();
        private MemEntry[]     MemoryMap;
        private readonly uint[] _rdramPageLastWriteEpoch = new uint[RdramPageCount];

        public uint LastViOriginWriteValue => _lastViOriginWriteValue;
        public uint LastViOriginWritePc => _lastViOriginWritePc;
        public uint LastPlausibleViOriginWriteValue => _lastPlausibleViOriginWriteValue;
        public uint LastPlausibleViOriginWritePc => _lastPlausibleViOriginWritePc;

        private void NoteRdramWriteRange(uint physicalAddress, uint size)
        {
            if (size == 0)
                return;

            uint baseAddress = physicalAddress & 0x1FFFFFFFu;
            if (baseAddress >= RDRAM.Length)
                return;

            uint endAddress = baseAddress + size - 1u;
            if (endAddress >= RDRAM.Length)
                endAddress = (uint)RDRAM.Length - 1u;

            uint epoch = ++_rdramWriteEpoch;
            int startPage = (int)(baseAddress / RdramPageSize);
            int endPage = (int)(endAddress / RdramPageSize);
            if (endPage >= _rdramPageLastWriteEpoch.Length)
                endPage = _rdramPageLastWriteEpoch.Length - 1;

            for (int page = startPage; page <= endPage; page++)
                _rdramPageLastWriteEpoch[page] = epoch;
        }

        public uint FindRecentFramebufferOriginCandidate(uint bufferSize, uint viOrigin, out ulong bestScore, out ulong viScore)
        {
            bestScore = 0;
            viScore = 0;

            if (bufferSize == 0 || bufferSize > RDRAM.Length)
                return viOrigin;

            int pagesNeeded = (int)((bufferSize + (RdramPageSize - 1u)) / RdramPageSize);
            if (pagesNeeded <= 0 || pagesNeeded > _rdramPageLastWriteEpoch.Length)
                return viOrigin;

            viScore = ScoreRecentFramebufferOrigin(viOrigin, pagesNeeded);
            bestScore = viScore;
            uint bestOrigin = viOrigin;

            int lastStartPage = _rdramPageLastWriteEpoch.Length - pagesNeeded;
            for (int startPage = 1; startPage <= lastStartPage; startPage++)
            {
                ulong score = 0;
                bool anyWrites = false;

                for (int page = 0; page < pagesNeeded; page++)
                {
                    uint epoch = _rdramPageLastWriteEpoch[startPage + page];
                    score += epoch;
                    anyWrites |= epoch != 0;
                }

                if (!anyWrites || score <= bestScore)
                    continue;

                bestScore = score;
                bestOrigin = (uint)startPage * RdramPageSize;
            }

            return bestOrigin;
        }

        private ulong ScoreRecentFramebufferOrigin(uint origin, int pagesNeeded)
        {
            uint baseAddress = origin & 0x1FFFFFFFu;
            if (baseAddress >= RDRAM.Length)
                return 0;

            uint endAddress = baseAddress + ((uint)pagesNeeded * RdramPageSize) - 1u;
            if (endAddress >= RDRAM.Length)
                endAddress = (uint)RDRAM.Length - 1u;

            int startPage = (int)(baseAddress / RdramPageSize);
            int endPage = (int)(endAddress / RdramPageSize);
            if (endPage >= _rdramPageLastWriteEpoch.Length)
                endPage = _rdramPageLastWriteEpoch.Length - 1;

            ulong score = 0;
            for (int page = startPage; page <= endPage; page++)
                score += _rdramPageLastWriteEpoch[page];
            return score;
        }

        private uint GetViLinesPerFrame()
        {
            uint viVSync = ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu;
            return (viVSync == 0) ? 0u : (viVSync + 1u);
        }

        private uint GetCpuCyclesPerViLine(uint viLinesPerFrame)
        {
            if (viLinesPerFrame == 0)
                return DefaultCpuCyclesPerViLine;

            uint cpuCyclesPerViLine = CpuCyclesPerViFrame / viLinesPerFrame;
            return (cpuCyclesPerViLine == 0) ? 1u : cpuCyclesPerViLine;
        }

        private void RefreshViCurrentRegister()
        {
            uint viVSync = ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu;
            if (viVSync == 0)
            {
                WriteBigEndianWord(VI_CURRENT_REG_RW, 0u);
                return;
            }

            uint viLinesPerFrame = viVSync + 1u;
            uint currentLine = _viCurrentLine % viLinesPerFrame;

            WriteBigEndianWord(VI_CURRENT_REG_RW, (currentLine & ~1u) | (_viField & 1u));
        }

        private void RecomputeViInterruptSchedule()
        {
            uint viVSync = ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu;
            uint viLinesPerFrame = (viVSync == 0) ? 0u : (viVSync + 1u);
            if (viLinesPerFrame == 0)
            {
                _viFrameDelayCycles = 0;
                _viInterruptCyclesRemaining = 0;
                return;
            }

            uint cpuCyclesPerViLine = GetCpuCyclesPerViLine(viLinesPerFrame);
            _viFrameDelayCycles = cpuCyclesPerViLine * viLinesPerFrame;
            if (_viFrameDelayCycles == 0)
                _viFrameDelayCycles = CpuCyclesPerViFrame;

            uint viIntrLine = ReadBigEndianWord(VI_INTR_REG_RW) & 0x03FFu;
            // VI only schedules a vertical interrupt when VI_INTR is strictly below
            // VI_V_SYNC. VI_INTR == VI_V_SYNC does not fire.
            if (viIntrLine >= viVSync)
            {
                _viInterruptCyclesRemaining = 0;
                return;
            }

            uint currentLine = _viCurrentLine % viLinesPerFrame;
            uint currentOffset = _viLineCycleAccum % cpuCyclesPerViLine;
            uint currentPos = currentLine * cpuCyclesPerViLine + currentOffset;
            uint targetPos = viIntrLine * cpuCyclesPerViLine;

            uint remaining = (targetPos > currentPos)
                ? (targetPos - currentPos)
                : (_viFrameDelayCycles - currentPos + targetPos);

            // Match edge-triggered "next event" behavior: if we're exactly on the target,
            // schedule the next frame's interrupt instead of firing continuously.
            _viInterruptCyclesRemaining = (remaining == 0) ? _viFrameDelayCycles : remaining;
        }

        public Memory(byte[] Rom)
        {
            _rom = Rom;
            _rspInterpreter = new RspInterpreter(this);

            // RDRAM (base + expansion/mirror window).
            // Keep backing array at 8 MiB for now; accesses beyond that window mirror via ResolveArrayOffset().
            MemoryMapList.Add(new MemEntry(0x00000000, 0x03EFFFFF, RDRAM, RDRAM,       "RDRAM"));
            MemoryMapList.Add(new MemEntry(0x03F00000, 0x03FFFFFF, RDRAMReg, RDRAMReg, "RDRAM Registers"));

            // SP Registers
            MemoryMapList.Add(new MemEntry(0x04000000, 0x04000FFF, SP_DMEM_RW,         SP_DMEM_RW,          "SP_DMEM"));
            MemoryMapList.Add(new MemEntry(0x04001000, 0x04001FFF, SP_IMEM_RW,         SP_IMEM_RW,          "SP_IMEM"));
            MemoryMapList.Add(new MemEntry(0x04040000, 0x04040003, SP_MEM_ADDR_REG_RW, SP_MEM_ADDR_REG_RW,  "SP_MEM_ADDR_REG",
                null, SP_MEM_ADDR_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04040004, 0x04040007, SP_DRAM_ADDR_REG_RW, SP_DRAM_ADDR_REG_RW, "SP_DRAM_ADDR_REG",
                null, SP_DRAM_ADDR_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04040008, 0x0404000B, SP_RD_LEN_REG_RW, SP_RD_LEN_REG_RW, "SP_RD_LEN_REG",
                null, SP_RD_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x0404000C, 0x0404000F, SP_WR_LEN_REG_RW, SP_WR_LEN_REG_RW, "SP_WR_LEN_REG",
                null, SP_WR_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04040010, 0x04040013, SP_STATUS_REG_R,    SP_STATUS_REG_W,     "SP_STATUS_REG",
                null, SP_STATUS_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04040014, 0x04040017, SP_DMA_FULL_REG_R,  SP_DMA_FULL_REG_W,   "SP_DMA_FULL_REG"));
            MemoryMapList.Add(new MemEntry(0x04040018, 0x0404001B, SP_DMA_BUSY_REG_R,  SP_DMA_BUSY_REG_W,   "SP_DMA_BUSY_REG"));
            MemoryMapList.Add(new MemEntry(0x0404001C, 0x0404001F, SP_SEMAPHORE_REG_R, SP_SEMAPHORE_REG_W,  "SP_SEMAPHORE_REG",
                SP_SEMAPHORE_READ_EVENT, null));
            MemoryMapList.Add(new MemEntry(0x04080000, 0x04080003, SP_PC_REG_RW,       SP_PC_REG_RW,        "SP_PC_REG",
                null, SP_PC_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04080004, 0x04080007, SP_IBIST_REG_RW,    SP_IBIST_REG_RW,     "SP_IBIST_REG"));

            // DPC Registers
            MemoryMapList.Add(new MemEntry(0x04100000, 0x04100003, DPC_START_REG_RW, DPC_START_REG_RW, "DPC_START_REG",
                null, DPC_START_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04100004, 0x04100007, DPC_END_REG_RW, DPC_END_REG_RW, "DPC_END_REG",
                null, DPC_END_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04100008, 0x0410000B, DPC_CURRENT_REG_RW, DPC_CURRENT_REG_RW, "DPC_CURRENT_REG"));
            MemoryMapList.Add(new MemEntry(0x0410000C, 0x0410000F, DPC_STATUS_REG_R, DPC_STATUS_REG_W, "DPC_STATUS_REG",
                null, DPC_STATUS_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04100010, 0x04100013, DPC_CLOCK_REG_RW, DPC_CLOCK_REG_RW, "DPC_CLOCK_REG"));
            MemoryMapList.Add(new MemEntry(0x04100014, 0x04100017, DPC_BUFBUSY_REG_RW, DPC_BUFBUSY_REG_RW, "DPC_BUFBUSY_REG"));
            MemoryMapList.Add(new MemEntry(0x04100018, 0x0410001B, DPC_PIPEBUSY_REG_RW, DPC_PIPEBUSY_REG_RW, "DPC_PIPEBUSY_REG"));
            MemoryMapList.Add(new MemEntry(0x0410001C, 0x0410001F, DPC_TMEM_REG_RW, DPC_TMEM_REG_RW, "DPC_TMEM_REG"));
            MemoryMapList.Add(new MemEntry(0x04200000, 0x04200003, DPS_TBIST_REG_RW, DPS_TBIST_REG_RW, "DPS_TBIST_REG"));
            MemoryMapList.Add(new MemEntry(0x04200004, 0x04200007, DPS_TEST_MODE_REG_RW, DPS_TEST_MODE_REG_RW, "DPS_TEST_MODE_REG"));
            MemoryMapList.Add(new MemEntry(0x04200008, 0x0420000B, DPS_BUFTEST_ADDR_REG_RW, DPS_BUFTEST_ADDR_REG_RW, "DPS_BUFTEST_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x0420000C, 0x0420000F, DPS_BUFTEST_DATA_REG_RW, DPS_BUFTEST_DATA_REG_RW, "DPS_BUFTEST_DATA_REG"));

            // MI Registers
            MemoryMapList.Add(new MemEntry(0x04300000, 0x04300003, MI_INIT_MODE_REG_R, MI_INIT_MODE_REG_W, "MI_INIT_MODE_REG",
                null, MI_INIT_MODE_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04300004, 0x04300007, MI_VERSION_REG_RW,  MI_VERSION_REG_RW,  "MI_VERSION_REG"));
            MemoryMapList.Add(new MemEntry(0x04300008, 0x0430000B, MI_INTR_REG_R,      null,               "MI_INTR_REG"));
            MemoryMapList.Add(new MemEntry(0x0430000C, 0x0430000F, MI_INTR_MASK_REG_R, MI_INTR_MASK_REG_W, "MI_INTR_MASK_REG",
                null, MI_INTR_MASK_WRITE_EVENT));

            // VI Registers
            MemoryMapList.Add(new MemEntry(0x04400000, 0x04400003, VI_STATUS_REG_RW,  VI_STATUS_REG_RW,  "VI_STATUS_REG",
                null, VI_STATUS_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400004, 0x04400007, VI_ORIGIN_REG_RW,  VI_ORIGIN_REG_RW,  "VI_ORIGIN_REG",
                null, VI_ORIGIN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400008, 0x0440000B, VI_WIDTH_REG_RW,   VI_WIDTH_REG_RW,   "VI_WIDTH_REG",
                null, VI_WIDTH_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x0440000C, 0x0440000F, VI_INTR_REG_RW,    VI_INTR_REG_RW,    "VI_INTR_REG",
                null, VI_INTR_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400010, 0x04400013, VI_CURRENT_REG_RW, VI_CURRENT_REG_RW, "VI_CURRENT_REG",
                VI_CURRENT_READ_EVENT, VI_CURRENT_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400014, 0x04400017, VI_BURST_REG_RW,   VI_BURST_REG_RW,   "VI_BURST_REG",
                null, VI_BURST_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400018, 0x0440001B, VI_V_SYNC_REG_RW,  VI_V_SYNC_REG_RW,  "VI_V_SYNC_REG",
                null, VI_V_SYNC_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x0440001C, 0x0440001F, VI_H_SYNC_REG_RW,  VI_H_SYNC_REG_RW,  "VI_H_SYNC_REG"));
            MemoryMapList.Add(new MemEntry(0x04400020, 0x04400023, VI_LEAP_REG_RW,    VI_LEAP_REG_RW,    "VI_LEAP_REG"));
            MemoryMapList.Add(new MemEntry(0x04400024, 0x04400027, VI_H_START_REG_RW, VI_H_START_REG_RW, "VI_H_START_REG",
                null, VI_H_START_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400028, 0x0440002B, VI_V_START_REG_RW, VI_V_START_REG_RW, "VI_V_START_REG",
                null, VI_V_START_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x0440002C, 0x0440002F, VI_V_BURST_REG_RW, VI_V_BURST_REG_RW, "VI_V_BURST_REG"));
            MemoryMapList.Add(new MemEntry(0x04400030, 0x04400033, VI_X_SCALE_REG_RW, VI_X_SCALE_REG_RW, "VI_X_SCALE_REG",
                null, VI_X_SCALE_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04400034, 0x04400037, VI_Y_SCALE_REG_RW, VI_Y_SCALE_REG_RW, "VI_Y_SCALE_REG",
                null, VI_Y_SCALE_WRITE_EVENT));

            // AI Registers
            MemoryMapList.Add(new MemEntry(0x04500000, 0x04500003, AI_DRAM_ADDR_REG_W, AI_DRAM_ADDR_REG_W, "AI_DRAM_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04500004, 0x04500007, AI_LEN_REG_RW,   AI_LEN_REG_RW,    "AI_LEN_REG",
                AI_LEN_READ_EVENT, AI_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04500008, 0x0450000B, AI_CONTROL_REG_W, AI_CONTROL_REG_W, "AI_CONTROL_REG"));
            MemoryMapList.Add(new MemEntry(0x0450000C, 0x0450000F, AI_STATUS_REG_R, AI_STATUS_REG_W,  "AI_STATUS_REG",
                null, AI_STATUS_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04500010, 0x04500013, AI_DACRATE_REG_W, AI_DACRATE_REG_W, "AI_DACRATE_REG"));
            MemoryMapList.Add(new MemEntry(0x04500014, 0x04500017, AI_BITRATE_REG_W, AI_BITRATE_REG_W, "AI_BITRATE_REG"));

            // PI Registers
            MemoryMapList.Add(new MemEntry(0x04600000, 0x04600003, PI_DRAM_ADDR_REG_RW, PI_DRAM_ADDR_REG_RW, "PI_DRAM_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04600004, 0x04600007, PI_CART_ADDR_REG_RW, PI_CART_ADDR_REG_RW, "PI_CART_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04600008, 0x0460000B, PI_RD_LEN_REG_RW, PI_RD_LEN_REG_RW,       "PI_RD_LEN_REG",
                null, PI_RD_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x0460000C, 0x0460000F, PI_WR_LEN_REG_RW, PI_WR_LEN_REG_RW,       "PI_WR_LEN_REG", 
                null, PI_WR_LEN_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04600010, 0x04600013, PI_STATUS_REG_R, PI_STATUS_REG_W,               "PI_STATUS_REG",
                PI_STATUS_READ_EVENT, PI_STATUS_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04600014, 0x04600017, PI_BSD_DOM1_LAT_REG_RW, PI_BSD_DOM1_LAT_REG_RW, "PI_BSD_DOM1_LAT_REG"));
            MemoryMapList.Add(new MemEntry(0x04600018, 0x0460001B, PI_BSD_DOM1_PWD_REG_RW, PI_BSD_DOM1_PWD_REG_RW, "PI_BSD_DOM1_PWD_REG"));
            MemoryMapList.Add(new MemEntry(0x0460001C, 0x0460001F, PI_BSD_DOM1_PGS_REG_RW, PI_BSD_DOM1_PGS_REG_RW, "PI_BSD_DOM1_PGS_REG"));
            MemoryMapList.Add(new MemEntry(0x04600020, 0x04600023, PI_BSD_DOM1_RLS_REG_RW, PI_BSD_DOM1_RLS_REG_RW, "PI_BSD_DOM1_RLS_REG"));
            MemoryMapList.Add(new MemEntry(0x04600024, 0x04600027, PI_BSD_DOM2_LAT_REG_RW, PI_BSD_DOM2_LAT_REG_RW, "PI_BSD_DOM2_LAT_REG"));
            MemoryMapList.Add(new MemEntry(0x04600028, 0x0460002B, PI_BSD_DOM2_PWD_REG_RW, PI_BSD_DOM2_PWD_REG_RW, "PI_BSD_DOM2_PWD_REG"));
            MemoryMapList.Add(new MemEntry(0x0460002C, 0x0460002F, PI_BSD_DOM2_PGS_REG_RW, PI_BSD_DOM2_PGS_REG_RW, "PI_BSD_DOM2_PGS_REG"));
            MemoryMapList.Add(new MemEntry(0x04600030, 0x04600033, PI_BSD_DOM2_RLS_REG_RW, PI_BSD_DOM2_RLS_REG_RW, "PI_BSD_DOM2_RLS_REG"));

            // SI Registers
            MemoryMapList.Add(new MemEntry(0x04800000, 0x04800003, SI_DRAM_ADDR_REG_RW, SI_DRAM_ADDR_REG_RW, "SI_DRAM_ADDR_REG"));
            MemoryMapList.Add(new MemEntry(0x04800004, 0x04800007, SI_PIF_ADDR_RD64B_REG_RW, SI_PIF_ADDR_RD64B_REG_RW, "SI_PIF_ADDR_RD64B_REG",
                null, SI_PIF_ADDR_RD64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04800010, 0x04800013, SI_PIF_ADDR_WR64B_REG_RW, SI_PIF_ADDR_WR64B_REG_RW, "SI_PIF_ADDR_WR64B_REG",
                null, SI_PIF_ADDR_WR64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04800018, 0x0480001B, SI_STATUS_REG_R, SI_STATUS_REG_W, "SI_STATUS_REG",
                null, SI_STATUS_WRITE_EVENT));
            // SI register alias window used by some boot/runtime paths.
            MemoryMapList.Add(new MemEntry(0x04818000, 0x04818003, SI_DRAM_ADDR_REG_RW, SI_DRAM_ADDR_REG_RW, "SI_DRAM_ADDR_REG_ALIAS"));
            MemoryMapList.Add(new MemEntry(0x04818004, 0x04818007, SI_PIF_ADDR_RD64B_REG_RW, SI_PIF_ADDR_RD64B_REG_RW, "SI_PIF_ADDR_RD64B_REG_ALIAS",
                null, SI_PIF_ADDR_RD64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04818010, 0x04818013, SI_PIF_ADDR_WR64B_REG_RW, SI_PIF_ADDR_WR64B_REG_RW, "SI_PIF_ADDR_WR64B_REG_ALIAS",
                null, SI_PIF_ADDR_WR64B_WRITE_EVENT));
            MemoryMapList.Add(new MemEntry(0x04818018, 0x0481801B, SI_STATUS_REG_R, SI_STATUS_REG_W, "SI_STATUS_REG_ALIAS",
                null, SI_STATUS_WRITE_EVENT));

            // RI Registers
            MemoryMapList.Add(new MemEntry(0x04700000, 0x04700003, RI_MODE_REG_RW, RI_MODE_REG_RW, "RI_MODE_REG"));
            MemoryMapList.Add(new MemEntry(0x04700004, 0x04700007, RI_CONFIG_REG_RW, RI_CONFIG_REG_RW, "RI_CONFIG_REG"));
            MemoryMapList.Add(new MemEntry(0x04700008, 0x0470000B, RI_CURRENT_LOAD_REG_RW, RI_CURRENT_LOAD_REG_RW, "RI_CURRENT_LOAD_REG"));
            MemoryMapList.Add(new MemEntry(0x0470000C, 0x0470000F, RI_SELECT_REG_RW, RI_SELECT_REG_RW, "RI_SELECT_REG"));
            MemoryMapList.Add(new MemEntry(0x04700010, 0x04700013, RI_REFRESH_REG_RW, RI_REFRESH_REG_RW, "RI_REFRESH_REG"));
            MemoryMapList.Add(new MemEntry(0x04700014, 0x04700017, RI_LATENCY_REG_RW, RI_LATENCY_REG_RW, "RI_LATENCY_REG"));
            MemoryMapList.Add(new MemEntry(0x04700018, 0x0470001B, RI_ERROR_REG_RW, RI_ERROR_REG_RW, "RI_ERROR_REG"));
            MemoryMapList.Add(new MemEntry(0x0470001C, 0x0470001F, RI_WERROR_REG_RW, RI_WERROR_REG_RW, "RI_WERROR_REG"));

            // Cartridge domains on PI bus.
            // For bring-up compatibility, map all domains to ROM data with mirroring.
            // This avoids boot/runtime loops on OpenBus when games probe alternate domains.
            MemoryMapList.Add(new MemEntry(0x05000000, 0x05FFFFFF, Rom, Rom, "Cartridge Domain 2 (Address 1)"));
            MemoryMapList.Add(new MemEntry(0x06000000, 0x07FFFFFF, Rom, Rom, "Cartridge Domain 1 (Address 1)"));
            MemoryMapList.Add(new MemEntry(0x08000000, 0x0FFFFFFF, Rom, Rom, "Cartridge Domain 2 (Address 2)"));
            MemoryMapList.Add(new MemEntry(0x10000000, 0x1FBFFFFF, Rom, Rom, "Cartridge Domain 1 (Address 2)"));
            MemoryMapList.Add(new MemEntry(0x1FC00800, 0x1FFFFFFF, Rom, Rom, "Cartridge Domain 1 (Address 2) Mirror"));

            // PIF
            MemoryMapList.Add(new MemEntry(0x1FC00000, 0x1FC007BF, PIFROM, PIFROM, "PIF Rom"));
            MemoryMapList.Add(new MemEntry(0x1FC007C0, 0x1FC007FF, PIFRAM, PIFRAM, "PIF Ram",
                null, PIF_RAM_WRITE_EVENT));

            MemoryMap = MemoryMapList.ToArray();
            MemoryMapList.Clear();

            // Setup Environment

            // MI Registers
            WriteBigEndianWord(MI_INIT_MODE_REG_R, 0x00000080); // MI_INIT_MODE_REG
            WriteUInt32Physical(0x04300004, 0x02020102); // MI_VERSION_REG

            // VI Registers
            WriteUInt32Physical(0x0440000C, 1023); // VI_INTR_REG

            // PI Registers
            uint BSD_DOM1_CONFIG = ReadUInt32Physical(0x10000000);

            WriteUInt32Physical(0x04600014, (BSD_DOM1_CONFIG      ) & 0xFF); // PI_BSD_DOM1_LAT_REG
            WriteUInt32Physical(0x04600018, (BSD_DOM1_CONFIG >> 8 ) & 0xFF); // PI_BSD_DOM1_PWD_REG
            WriteUInt32Physical(0x0460001C, (BSD_DOM1_CONFIG >> 16) & 0x0F); // PI_BSD_DOM1_PGS_REG
            WriteUInt32Physical(0x04600020, (BSD_DOM1_CONFIG >> 20) & 0x03); // PI_BSD_DOM1_RLS_REG
            // Keep DOM2 initialized to sane defaults (same profile as DOM1 during bring-up).
            WriteUInt32Physical(0x04600024, (BSD_DOM1_CONFIG      ) & 0xFF); // PI_BSD_DOM2_LAT_REG
            WriteUInt32Physical(0x04600028, (BSD_DOM1_CONFIG >> 8 ) & 0xFF); // PI_BSD_DOM2_PWD_REG
            WriteUInt32Physical(0x0460002C, (BSD_DOM1_CONFIG >> 16) & 0x0F); // PI_BSD_DOM2_PGS_REG
            WriteUInt32Physical(0x04600030, (BSD_DOM1_CONFIG >> 20) & 0x03); // PI_BSD_DOM2_RLS_REG

            WriteBigEndianWord(SP_STATUS_REG_R, SpStatusHalt);
            WriteBigEndianWord(SP_RD_LEN_REG_RW, 0x00000FF8u);
            WriteBigEndianWord(SP_WR_LEN_REG_RW, 0x00000FF8u);
            WriteBigEndianWord(SP_DMA_BUSY_REG_R, 0);
            WriteBigEndianWord(SP_DMA_BUSY_REG_W, 0);

            // RI Registers
            WriteUInt32Physical(0x0470000C, 0b1110); // RI_SELECT_REG

            // Copy the boot code to SP_DMEM using physical addresses.
            // This must bypass data-side TLB translation during early boot.
            DmaCopyPhysical(0x04000040, 0x10000040, 0xFC0);

            // Required by CIC x105
            WriteUInt32Physical(0x40001000, 0x3C0DBFC0);
            WriteUInt32Physical(0x40001004, 0x8DA807FC);
            WriteUInt32Physical(0x40001008, 0x25AD07C0);
            WriteUInt32Physical(0x4000100C, 0x31080080);
            WriteUInt32Physical(0x40001010, 0x5500FFFC);
            WriteUInt32Physical(0x40001014, 0x3C0DBFC0);
            WriteUInt32Physical(0x40001018, 0x8DA80024);
            WriteUInt32Physical(0x4000101C, 0x3C0BB000);
        }

        public void Tick(uint cpuCycles)
        {
            AdvanceRspDpLifecycle(cpuCycles);
            AdvancePiLifecycle(cpuCycles);
            AdvanceSiLifecycle(cpuCycles);
            AdvanceAiLifecycle(cpuCycles);

            uint viVSync = ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu;
            if (viVSync == 0)
            {
                _viCurrentLine = 0;
                _viLineCycleAccum = 0;
                _viFrameDelayCycles = 0;
                _viInterruptCyclesRemaining = 0;
                WriteBigEndianWord(VI_CURRENT_REG_RW, 0u);
                return;
            }

            uint viLinesPerFrame = viVSync + 1u;
            uint cpuCyclesPerViLine = GetCpuCyclesPerViLine(viLinesPerFrame);
            uint viFrameDelayCycles = cpuCyclesPerViLine * viLinesPerFrame;
            if (viFrameDelayCycles == 0)
                viFrameDelayCycles = CpuCyclesPerViFrame;

            if (_viFrameDelayCycles != viFrameDelayCycles)
                RecomputeViInterruptSchedule();

            _viLineCycleAccum += cpuCycles;
            while (_viLineCycleAccum >= cpuCyclesPerViLine)
            {
                _viLineCycleAccum -= cpuCyclesPerViLine;
                _viCurrentLine++;
                if (_viCurrentLine >= viLinesPerFrame)
                    _viCurrentLine = 0;

                RefreshViCurrentRegister();
            }

            uint viIntrLine = ReadBigEndianWord(VI_INTR_REG_RW) & 0x03FFu;
            if (viIntrLine >= viVSync || _viFrameDelayCycles == 0)
                return;

            if (_viInterruptCyclesRemaining == 0)
                RecomputeViInterruptSchedule();

            if (cpuCycles >= _viInterruptCyclesRemaining)
            {
                uint remaining = cpuCycles;
                while (remaining >= _viInterruptCyclesRemaining)
                {
                    remaining -= _viInterruptCyclesRemaining;
                    _viField ^= (ReadBigEndianWord(VI_STATUS_REG_RW) >> 6) & 0x1u;
                    RefreshViCurrentRegister();
                    SetMiViInterrupt(immediate: true);
                    _viInterruptCyclesRemaining = _viFrameDelayCycles;
                }

                if (remaining != 0)
                    _viInterruptCyclesRemaining -= remaining;
            }
            else
            {
                _viInterruptCyclesRemaining -= cpuCycles;
            }
        }

        private void AdvancePiLifecycle(uint cpuCycles)
        {
            if (!_piInterruptDelayArmed)
                return;

            if (cpuCycles >= _piInterruptDelayRemaining)
            {
                _piInterruptDelayRemaining = 0;
                _piInterruptDelayArmed = false;
                FinalizePiDmaCompletion();
            }
            else
            {
                _piInterruptDelayRemaining -= cpuCycles;
            }
        }

        private void ArmPiDmaCompletion(uint transferBytes)
        {
            if (_piDmaBusy || _piInterruptDelayArmed)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64PIDMA] suppress-rearm pc=0x{Registers.R4300.PC:x8} " +
                        $"transferBytes=0x{transferBytes:x} busy={_piDmaBusy} armed={_piInterruptDelayArmed} " +
                        $"delay=0x{_piInterruptDelayRemaining:x} " +
                        $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
                }
                return;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] arm pc=0x{Registers.R4300.PC:x8} " +
                    $"transferBytes=0x{transferBytes:x} busyBefore={_piDmaBusy} " +
                    $"armedBefore={_piInterruptDelayArmed} delayBefore=0x{_piInterruptDelayRemaining:x} " +
                    $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8}");
            }

            _piDmaBusy = true;

            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            piStatus |= PiStatusDmaBusy;
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);

            _piInterruptDelayArmed = true;
            _piInterruptDelayRemaining = Math.Max(PiDmaCyclesMinimum, transferBytes / 8u);
        }

        private void FinalizePiDmaCompletion()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] complete-enter pc=0x{Registers.R4300.PC:x8} " +
                    $"busyBefore={_piDmaBusy} dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} " +
                    $"cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8} " +
                    $"piStatusBefore=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntrBefore=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            _piDmaBusy = false;

            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            piStatus &= ~(PiStatusDmaBusy | PiStatusIoBusy);
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);

            SetMiPiInterrupt(immediate: true);

            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] complete-exit pc=0x{Registers.R4300.PC:x8} " +
                    $"busyAfter={_piDmaBusy} piStatusAfter=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} " +
                    $"miIntrAfter=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
        }

        private bool ValidatePiRequest(string source, uint rawLength, uint dramAddr, uint cartAddr)
        {
            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            bool busy = (piStatus & (PiStatusDmaBusy | PiStatusIoBusy)) != 0
                || _piDmaBusy
                || _piInterruptDelayArmed;
            if (!busy)
                return true;

            piStatus |= PiStatusError;
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);

            if (TracePiInterruptLifecycle
                || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIIRQ] reject source={source} pc=0x{Registers.R4300.PC:x8} " +
                    $"rawLen=0x{rawLength:x6} dram=0x{dramAddr:x8} cart=0x{cartAddr:x8} " +
                    $"piStatus=0x{piStatus:x8} busy={_piDmaBusy} armed={_piInterruptDelayArmed} " +
                    $"delay=0x{_piInterruptDelayRemaining:x}");
            }

            return false;
        }

        private void AdvanceSiLifecycle(uint cpuCycles)
        {
            if (!_siInterruptDelayArmed)
                return;

            if (cpuCycles >= _siInterruptDelayRemaining)
            {
                _siInterruptDelayRemaining = 0;
                _siInterruptDelayArmed = false;
                FinalizeSiDmaCompletion();
            }
            else
            {
                _siInterruptDelayRemaining -= cpuCycles;
            }
        }

        private void ArmSiDmaCompletion(bool readToDram, uint dramAddr)
        {
            _siDmaActive = true;
            _siDmaReadToDram = readToDram;
            _siDramAddr = dramAddr & 0x00FFFFF8u;
            SetSiBusy(SiStatusDmaBusy);
            _siInterruptDelayArmed = true;
            _siInterruptDelayRemaining = SiDmaDurationCycles;

            if (TraceSiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SIIRQ] arm pc=0x{Registers.R4300.PC:x8} readToDram={readToDram} dram=0x{_siDramAddr:x8} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"delay=0x{_siInterruptDelayRemaining:x}");
            }
        }

        private void FinalizeSiDmaCompletion()
        {
            if (!_siDmaActive && !_siDirectPifWriteActive)
                return;

            bool directPifWrite = _siDirectPifWriteActive;
            bool readToDram = _siDmaReadToDram;

            if (_siDirectPifWriteActive)
            {
                ProcessPifControlFlags();
            }
            else if (_siDmaReadToDram)
            {
                uint dramKseg1 = PhysicalToKseg1(_siDramAddr);
                const int size = 64;
                WithWriteUInt8Origin("si-dma-read", () =>
                {
                    for (uint i = 0; i < size; i++)
                        WriteUInt8(dramKseg1 + i, PIFRAM[i]);
                });
            }
            else
            {
                ProcessPifJoybusCommands();
            }

            _siDmaActive = false;
            _siDirectPifWriteActive = false;
            SetSiBusy(0);
            WriteBigEndianWord(SI_PIF_ADDR_RD64B_REG_RW, 0);
            WriteBigEndianWord(SI_PIF_ADDR_WR64B_REG_RW, 0);
            WriteBigEndianWord(SI_DRAM_ADDR_REG_RW, 0);

            if (TraceSiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SIIRQ] complete pc=0x{Registers.R4300.PC:x8} directPif={directPifWrite} readToDram={readToDram} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} pifCtl=0x{PIFRAM[63]:x2} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SI DMA completion directPif={directPifWrite} readToDram={readToDram} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} pifCtl=0x{PIFRAM[63]:x2} pc=0x{Registers.R4300.PC:x8}");
            }

            SetMiSiInterrupt(immediate: true);
        }

        private void AdvanceRspDpLifecycle(uint cpuCycles)
        {
            if (_spDmaDelayArmed)
            {
                if (cpuCycles >= _spDmaDelayRemaining)
                {
                    _spDmaDelayRemaining = 0;
                    _spDmaDelayArmed = false;
                    FinalizeSpDma();
                }
                else
                {
                    _spDmaDelayRemaining -= cpuCycles;
                }
            }

            if (_rspTaskActive)
            {
                if (cpuCycles >= _rspTaskCyclesRemaining)
                {
                    _rspTaskCyclesRemaining = 0;
                    CompleteRspTask();
                }
                else
                {
                    _rspTaskCyclesRemaining -= cpuCycles;
                }
            }

            if (_rspInterruptDelayArmed)
            {
                if (cpuCycles >= _rspInterruptDelayRemaining)
                {
                    _rspInterruptDelayRemaining = 0;
                    _rspInterruptDelayArmed = false;
                    FinalizeRspInterrupt();
                }
                else
                {
                    _rspInterruptDelayRemaining -= cpuCycles;
                }
            }

            if (_dpInterruptDelayArmed)
            {
                if (cpuCycles >= _dpInterruptDelayRemaining)
                {
                    _dpInterruptDelayRemaining = 0;
                    _dpInterruptDelayArmed = false;
                    FinalizeDpInterrupt();
                }
                else
                {
                    _dpInterruptDelayRemaining -= cpuCycles;
                }
            }
        }

        private void FinalizeSpDma()
        {
            if (_spQueuedDmaValid)
            {
                SpDmaRequest queued = _spQueuedDma;
                _spQueuedDmaValid = false;
                _spDmaFull = false;
                WriteBigEndianWord(SP_DMA_FULL_REG_R, 0);
                uint queuedStatus = ReadBigEndianWord(SP_STATUS_REG_R) & ~0x00000008u;
                WriteBigEndianWord(SP_STATUS_REG_R, queuedStatus);

                if (IsTraceN64SpDmaEnabled())
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64SPDMA] dequeue pc=0x{Registers.R4300.PC:x8} " +
                        $"read={queued.IsReadFromDram} mem=0x{queued.MemAddr:x8} dram=0x{queued.DramAddr:x8} len=0x{queued.LengthReg:x8} " +
                        $"liveMem=0x{ReadBigEndianWord(SP_MEM_ADDR_REG_RW):x8} liveDram=0x{ReadBigEndianWord(SP_DRAM_ADDR_REG_RW):x8} " +
                        $"liveLen=0x{ReadBigEndianWord(queued.IsReadFromDram ? SP_RD_LEN_REG_RW : SP_WR_LEN_REG_RW):x8}");
                }

                ExecuteSpDma(queued);
                return;
            }

            _spDmaBusy = false;
            WriteBigEndianWord(SP_DMA_BUSY_REG_R, 0);
            uint status = ReadBigEndianWord(SP_STATUS_REG_R) & ~SpStatusDmaBusy;
            WriteBigEndianWord(SP_STATUS_REG_R, status);
        }

        private void CompleteRspTask()
        {
            _rspTaskActive = false;

            uint status = ReadBigEndianWord(SP_STATUS_REG_R);
            _rspInterruptDelayArmed = true;
            _rspInterruptDelayRemaining = GetRspInterruptDelayCycles(_activeRspTask.Type);

            if (_activeRspTask.Type == 1)
            {
                FinalizeGraphicsTask();
                _dpInterruptDelayArmed = true;
                _dpInterruptDelayRemaining = 4000;
            }

            // Mupen's synchronous RSP/plugin completion path exposes HALT/BROKE/TASKDONE
            // immediately and does not keep rsp_task_locked asserted while waiting for
            // the delayed SP interrupt delivery. Match that behavior here so the guest
            // can observe completion and queue follow-up work without waiting for MI.
            _rspTaskLocked = false;
            status |= SpStatusTaskDone | SpStatusBroke | SpStatusHalt;
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP task completed type={_activeRspTask.Type} pc=0x{Registers.R4300.PC:x8} " +
                    $"spStatus=0x{status:x8} rspIntDelay={_rspInterruptDelayRemaining} dpDelay={_dpInterruptDelayRemaining} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} " +
                    $"dpcStatus=0x{ReadBigEndianWord(DPC_STATUS_REG_R):x8}");
            }
        }

        private void FinalizeRspInterrupt()
        {
            uint status = ReadBigEndianWord(SP_STATUS_REG_R);
            _rspTaskLocked = false;
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] FinalizeRspInterrupt preRaise type={_activeRspTask.Type} pc=0x{Registers.R4300.PC:x8} " +
                    $"spStatus=0x{status:x8} intrBreak={(status & SpStatusIntrBreak) != 0} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8}");
            }

            if ((status & SpStatusIntrBreak) != 0)
                SetMiSpInterrupt(immediate: true);
        }

        private void FinalizeDpInterrupt()
        {
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] FinalizeDpInterrupt preRaise type={_activeRspTask.Type} pc=0x{Registers.R4300.PC:x8} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} " +
                    $"dpcStatus=0x{ReadBigEndianWord(DPC_STATUS_REG_R):x8}");
            }
            SetMiDpInterrupt(immediate: true);
        }

        private void FinalizeGraphicsTask()
        {
            uint start = ReadBigEndianWord(DPC_START_REG_RW);
            uint end = ReadBigEndianWord(DPC_END_REG_RW);
            WriteBigEndianWord(DPC_CURRENT_REG_RW, end);

            uint status = ReadBigEndianWord(DPC_STATUS_REG_R);
            status &= ~(DpcStatusCbufReady | DpcStatusStartValid | DpcStatusEndValid);
            WriteBigEndianWord(DPC_STATUS_REG_R, status);
            WriteBigEndianWord(DPC_BUFBUSY_REG_RW, 0);
            WriteBigEndianWord(DPC_PIPEBUSY_REG_RW, 0);

            // A successful task completion does not proactively zero the guest-owned
            // yield buffer.
            // Clearing it here can corrupt unrelated RDRAM when the pointer is stale,
            // segmented, or otherwise not a valid yield target for the completed task.

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] Graphics task finalized dpcStart=0x{start:x8} dpcEnd=0x{end:x8} " +
                    $"yield=0x{_activeRspTask.YieldDataPtr:x8}/0x{_activeRspTask.YieldDataSize:x} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void ClearPhysicalRange(uint physicalAddress, uint length)
        {
            uint baseAddress = physicalAddress & 0x1FFFFFFFu;
            WithWriteUInt8Origin("clear-physical-range", () =>
            {
                for (uint i = 0; i < length; i++)
                    WriteUInt8(PhysicalToKseg1(baseAddress + i), 0);
            });
        }

        private static uint GetRspExecutionCycles(uint taskType)
        {
            if (taskType == 1)
                return 1000; // graphics
            if (taskType == 2)
                return 4000; // audio
            return 250;
        }

        private static uint GetRspInterruptDelayCycles(uint taskType)
        {
            if (taskType == 1)
                return 1000;
            if (taskType == 2)
                return 4000;
            return 0;
        }

        private uint GetAiSampleRate()
        {
            uint dacRate = ReadBigEndianWord(AI_DACRATE_REG_W) & 0x3FFFu;
            if (dacRate == 0)
                return 44_100u;

            const double N64NtscClock = 48_681_812.0;
            uint rate = (uint)Math.Round(N64NtscClock / (dacRate + 1.0));
            if (rate < 4_000u) rate = 4_000u;
            if (rate > 96_000u) rate = 96_000u;
            return rate;
        }

        private uint GetAiDmaDuration(uint length)
        {
            if (length == 0)
                return 0;

            uint sampleRate = GetAiSampleRate();
            if (sampleRate == 0)
                sampleRate = 44_100u;

            uint cpuCountsPerSecond = CpuCyclesPerSecond;
            uint viDelay = _viFrameDelayCycles;
            if (viDelay != 0)
                cpuCountsPerSecond = viDelay * 60u;

            ulong duration = ((ulong)length * cpuCountsPerSecond) / (4UL * sampleRate);
            if (duration == 0)
                duration = 1;

            return (uint)Math.Min(duration, uint.MaxValue);
        }

        private void StartAiFifo0()
        {
            if (_aiFifo0Length == 0)
                return;

            uint status = ReadBigEndianWord(AI_STATUS_REG_R) | AiStatusBusy;
            WriteBigEndianWord(AI_STATUS_REG_R, status);
            _aiInterruptDelayArmed = true;
            _aiInterruptDelayRemaining = _aiFifo0Duration != 0 ? _aiFifo0Duration : 1u;
        }

        private void PushAiFifo(uint address, uint length)
        {
            uint duration = GetAiDmaDuration(length);
            uint status = ReadBigEndianWord(AI_STATUS_REG_R);
            address &= 0x00FFFFF8u;
            length &= ~7u;

            if (length == 0)
                return;

            if ((status & AiStatusBusy) != 0)
            {
                _aiFifo1Address = address;
                _aiFifo1Length = length;
                _aiFifo1Duration = duration;
                status |= AiStatusFull;
                WriteBigEndianWord(AI_STATUS_REG_R, status);
            }
            else
            {
                _aiFifo0Address = address;
                _aiFifo0Length = length;
                _aiFifo0Duration = duration;
                _aiDelayedCarry = false;
                WriteBigEndianWord(AI_DRAM_ADDR_REG_W, address);
                WriteBigEndianWord(AI_LEN_REG_RW, length);
                WriteBigEndianWord(AI_STATUS_REG_R, status & ~AiStatusFull);
                StartAiFifo0();
            }
        }

        private uint GetAiRemainingLength()
        {
            uint status = ReadBigEndianWord(AI_STATUS_REG_R);
            if ((status & AiStatusBusy) == 0 || _aiFifo0Length == 0 || _aiFifo0Duration == 0)
                return 0;

            uint remaining = _aiInterruptDelayRemaining;
            if (remaining >= _aiFifo0Duration)
                return _aiFifo0Length & ~7u;

            ulong length = ((ulong)remaining * _aiFifo0Length) / _aiFifo0Duration;
            return (uint)length & ~7u;
        }

        private static bool AiNeedsDelayedCarry(uint address, uint length)
        {
            return (((address + length) & 0x1FFFu) == 0);
        }

        private void AdvanceAiLifecycle(uint cpuCycles)
        {
            if (!_aiInterruptDelayArmed)
                return;

            if (cpuCycles >= _aiInterruptDelayRemaining)
            {
                _aiInterruptDelayRemaining = 0;
                _aiInterruptDelayArmed = false;
                FinalizeAiDmaCompletion();
            }
            else
            {
                _aiInterruptDelayRemaining -= cpuCycles;
            }
        }

        private void FinalizeAiDmaCompletion()
        {
            uint status = ReadBigEndianWord(AI_STATUS_REG_R);
            _aiDelayedCarry = AiNeedsDelayedCarry(_aiFifo0Address, _aiFifo0Length);

            if (_aiFifo1Length != 0)
            {
                _aiFifo0Address = _aiFifo1Address;
                if (_aiDelayedCarry)
                    _aiFifo0Address = (_aiFifo0Address + 0x2000u) & 0x00FFFFF8u;
                _aiFifo0Length = _aiFifo1Length;
                _aiFifo0Duration = _aiFifo1Duration;
                _aiFifo1Address = 0;
                _aiFifo1Length = 0;
                _aiFifo1Duration = 0;
                status &= ~AiStatusFull;
                WriteBigEndianWord(AI_STATUS_REG_R, status);
                WriteBigEndianWord(AI_DRAM_ADDR_REG_W, _aiFifo0Address);
                WriteBigEndianWord(AI_LEN_REG_RW, _aiFifo0Length);
                StartAiFifo0();
            }
            else
            {
                _aiFifo0Address = 0;
                _aiFifo0Length = 0;
                _aiFifo0Duration = 0;
                _aiDelayedCarry = false;
                status &= ~(AiStatusBusy | AiStatusFull);
                WriteBigEndianWord(AI_STATUS_REG_R, status);
                WriteBigEndianWord(AI_LEN_REG_RW, 0);
            }

            SetMiAiInterrupt();
        }

        public void AI_LEN_READ_EVENT()
        {
            WriteBigEndianWord(AI_LEN_REG_RW, GetAiRemainingLength());
        }

        public void AI_LEN_WRITE_EVENT()
        {
            uint length = ReadBigEndianWord(AI_LEN_REG_RW) & ~7u;
            uint address = ReadBigEndianWord(AI_DRAM_ADDR_REG_W) & 0x00FFFFFFu;
            if (length == 0)
                return;

            PushAiFifo(address, length);
        }

        public void AI_STATUS_WRITE_EVENT()
        {
            ClearMiAiInterrupt();
        }

        public void PI_WR_LEN_WRITE_EVENT()
        {
            uint WriteLength = ReadUInt32Physical(0x0460000C) & 0x00FFFFFF; // PI_WR_LEN_REG
            uint CartAddr    = ReadUInt32Physical(0x04600004) & 0x1FFFFFFE; // PI_CART_ADDR_REG
            uint DramAddr    = ReadUInt32Physical(0x04600000) & 0x00FFFFFE; // PI_DRAM_ADDR_REG
            if (!ValidatePiRequest("wr", WriteLength, DramAddr, CartAddr))
                return;
            uint normalizedLength = NormalizePiTransferLength(WriteLength, DramAddr, isWriteToDram: true);
            if (TracePiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIIRQ] wr-start pc=0x{Registers.R4300.PC:x8} rawLen=0x{WriteLength:x6} normLen=0x{normalizedLength:x6} " +
                    $"dram=0x{DramAddr:x8} cart=0x{CartAddr:x8} piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] wr-start pc=0x{Registers.R4300.PC:x8} " +
                    $"rawLen=0x{WriteLength:x6} normLen=0x{normalizedLength:x6} " +
                    $"dram=0x{DramAddr:x8} cart=0x{CartAddr:x8} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI_WR_LEN write len=0x{WriteLength:x6} normalized=0x{normalizedLength:x6} " +
                    $"cart=0x{CartAddr:x8} dram=0x{DramAddr:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            int transferSize = ComputePiTransferSize(normalizedLength, DramAddr, CartAddr);
            if (transferSize > 0)
                DmaCopyPhysical(DramAddr, CartAddr, transferSize);

            FinalizePiWriteTransfer(normalizedLength, DramAddr, CartAddr, transferSize);
            ArmPiDmaCompletion((WriteLength & 0x00FFFFFFu) + 1u);
        }

        public void PI_RD_LEN_WRITE_EVENT()
        {
            // PI_RD_LEN is RDRAM -> cart/peripheral on hardware and in Mupen.
            // Most cart-side writeback paths are not implemented here yet, so default behavior stays
            // non-destructive while still honoring timing/IRQ semantics. The mirror mode remains
            // available as a bring-up knob when a title expects permissive cart->dram behavior.
            uint ReadLength = ReadUInt32Physical(0x04600008) & 0x00FFFFFF; // PI_RD_LEN_REG
            uint CartAddr   = ReadUInt32Physical(0x04600004) & 0x1FFFFFFE; // PI_CART_ADDR_REG
            uint DramAddr   = ReadUInt32Physical(0x04600000) & 0x00FFFFFE; // PI_DRAM_ADDR_REG
            if (!ValidatePiRequest("rd", ReadLength, DramAddr, CartAddr))
                return;
            uint normalizedLength = NormalizePiTransferLength(ReadLength, DramAddr, isWriteToDram: false);
            if (TracePiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIIRQ] rd-start pc=0x{Registers.R4300.PC:x8} rawLen=0x{ReadLength:x6} normLen=0x{normalizedLength:x6} " +
                    $"dram=0x{DramAddr:x8} cart=0x{CartAddr:x8} mirror={MirrorPiRdLenAsCartToDram} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] rd-start pc=0x{Registers.R4300.PC:x8} " +
                    $"rawLen=0x{ReadLength:x6} normLen=0x{normalizedLength:x6} " +
                    $"dram=0x{DramAddr:x8} cart=0x{CartAddr:x8} mirror={MirrorPiRdLenAsCartToDram} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI_RD_LEN write len=0x{ReadLength:x6} normalized=0x{normalizedLength:x6} " +
                    $"cart=0x{CartAddr:x8} dram=0x{DramAddr:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            if (MirrorPiRdLenAsCartToDram)
            {
                int transferSize = ComputePiTransferSize(normalizedLength, DramAddr, CartAddr);
                if (transferSize > 0)
                    DmaCopyPhysical(DramAddr, CartAddr, transferSize);
            }

            FinalizePiReadTransfer(normalizedLength, DramAddr, CartAddr);
            ArmPiDmaCompletion((ReadLength & 0x00FFFFFFu) + 1u);
        }

        public void PI_STATUS_READ_EVENT()
        {
            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            if (_piDmaBusy)
                piStatus |= PiStatusDmaBusy;
            else
                piStatus &= ~PiStatusDmaBusy;
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);

            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_STATUS_LATE"), "1", StringComparison.Ordinal)
                && ((Registers.R4300.PC >= 0x80025DF0u && Registers.R4300.PC <= 0x80025E20u)
                    || (Registers.R4300.PC >= 0x8008A020u && Registers.R4300.PC <= 0x8008A0F0u)
                    || (Registers.R4300.PC >= 0x80091FD0u && Registers.R4300.PC <= 0x80092010u)
                    || (Registers.R4300.PC >= 0x80092EA8u && Registers.R4300.PC <= 0x80092EC4u)
                    || (Registers.R4300.PC >= 0x8009A460u && Registers.R4300.PC <= 0x8009A490u)
                    || (Registers.R4300.PC >= 0x8009B900u && Registers.R4300.PC <= 0x8009B980u)))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PISTATUSR] pc=0x{Registers.R4300.PC:x8} status=0x{piStatus:x8} " +
                    $"busy={_piDmaBusy} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} " +
                    $"piDram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} piCart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
            }
        }

        private static uint NormalizePiTransferLength(uint lengthReg, uint dramAddr, bool isWriteToDram)
        {
            uint length = (lengthReg & 0x00FFFFFFu) + 1u;

            // Keep early PI unaligned-transfer behavior tolerant enough for boot code.
            if (length >= 0x7Fu && (length & 1u) != 0)
                length++;

            if (isWriteToDram && length <= 0x80u)
            {
                uint trim = dramAddr & 0x7u;
                length = (length > trim) ? (length - trim) : 0u;
            }

            return length;
        }

        private int ComputePiTransferSize(uint normalizedLength, uint dramAddr, uint cartAddr)
        {
            long requested = normalizedLength;
            if (requested <= 0)
                return 0;

            long rdramLeft = RDRAM.Length - (dramAddr & 0x007FFFFF);
            long cartLeft = int.MaxValue;
            if (cartAddr >= 0x10000000 && cartAddr <= 0x1FFFFFFF)
                cartLeft = Math.Max(0L, (long)GetEntry(cartAddr).ReadArray.Length - (cartAddr - 0x10000000));

            long maxSafe = Math.Min(requested, Math.Min(rdramLeft, cartLeft));
            const int MaxPiTransferPerOp = 1 << 20; // 1 MiB safety cap for bring-up
            if (maxSafe > MaxPiTransferPerOp)
                maxSafe = MaxPiTransferPerOp;

            if (maxSafe < 0)
                return 0;

            return (int)maxSafe;
        }

        private void FinalizePiWriteTransfer(uint normalizedLength, uint dramAddr, uint cartAddr, int transferSize)
        {
            // Keep the common PI writeback quirk tolerant enough for boot code.
            uint writeback = 0x7Fu;
            if (transferSize > 0 && transferSize <= 8)
                writeback = (uint)Math.Max(0, 0x7F - (int)(dramAddr & 7u));

            WriteBigEndianWord(PI_WR_LEN_REG_RW, writeback);

            uint transferred = (uint)Math.Max(transferSize, 0);
            if (transferred == 0)
                transferred = normalizedLength;

            uint advancedDram = (dramAddr + transferred + 7u) & ~7u;
            uint advancedCart = (cartAddr + transferred + 1u) & ~1u;
            WriteBigEndianWord(PI_DRAM_ADDR_REG_RW, advancedDram & 0x00FFFFFEu);
            WriteBigEndianWord(PI_CART_ADDR_REG_RW, advancedCart & 0x1FFFFFFEu);
        }

        private void FinalizePiReadTransfer(uint normalizedLength, uint dramAddr, uint cartAddr)
        {
            WriteBigEndianWord(PI_RD_LEN_REG_RW, 0x7Fu);

            uint advancedDram = (dramAddr + normalizedLength + 7u) & ~7u;
            uint advancedCart = (cartAddr + normalizedLength + 1u) & ~1u;
            WriteBigEndianWord(PI_DRAM_ADDR_REG_RW, advancedDram & 0x00FFFFFEu);
            WriteBigEndianWord(PI_CART_ADDR_REG_RW, advancedCart & 0x1FFFFFFEu);
        }

        public void PI_STATUS_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(PI_STATUS_REG_W);
            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI_STATUS write value=0x{value:x8} old=0x{piStatus:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            if (TraceMegaStatusBlock || TraceMegaCallbackBlock || TraceMegaFatalBlock
                || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_LATE_WINDOW"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64MEGALATEIO] PI_STATUS write value=0x{value:x8} old=0x{piStatus:x8} pc=0x{Registers.R4300.PC:x8} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} viCurrent=0x{ReadBigEndianWord(VI_CURRENT_REG_RW):x8} " +
                    $"piDram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} piCart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
            }

            // Mupen behavior: bit1 clears PI interrupt, bit0 resets PI status.
            if ((value & 0x00000002) != 0)
            {
                _piIrqClearCount++;
                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64PIDMA] status-clear pc=0x{Registers.R4300.PC:x8} value=0x{value:x8} " +
                        $"oldPiStatus=0x{piStatus:x8} miIntrBefore=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                        $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
                }

                if (TracePiInterruptLifecycle)
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64PIIRQ] clear-write#{_piIrqClearCount} pc=0x{Registers.R4300.PC:x8} " +
                        $"value=0x{value:x8} oldStatus=0x{piStatus:x8} miIntrBefore=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                        $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
                }

                piStatus &= ~PiStatusInterrupt;
                ClearMiPiInterrupt();

                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64PIDMA] status-clear-done pc=0x{Registers.R4300.PC:x8} " +
                        $"newPiStatus=0x{piStatus:x8} miIntrAfter=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
                }
            }

            if ((value & 0x00000001) != 0)
            {
                piStatus = 0;
                _piDmaBusy = false;
                _piInterruptDelayArmed = false;
                _piInterruptDelayRemaining = 0;
            }

            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine($"[N64IO] PI_STATUS new=0x{piStatus:x8}");
            }
        }

        public void SI_PIF_ADDR_RD64B_WRITE_EVENT()
        {
            // PIF RAM -> RDRAM (64 bytes)
            uint dramAddr = ReadUInt32Physical(0x04800000) & 0x00FFFFF8;
            if (_siDmaActive)
            {
                uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R) | 0x00000008u;
                WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
                return;
            }

            ArmSiDmaCompletion(readToDram: true, dramAddr);
        }

        public void SI_PIF_ADDR_WR64B_WRITE_EVENT()
        {
            // RDRAM -> PIF RAM (64 bytes)
            uint dramAddr = ReadUInt32Physical(0x04800000) & 0x00FFFFF8;
            uint dramKseg1 = PhysicalToKseg1(dramAddr);
            const int size = 64;
            if (_siDmaActive)
            {
                uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R) | 0x00000008u;
                WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
                return;
            }

            for (uint i = 0; i < size; i++)
                PIFRAM[i] = ReadUInt8(dramKseg1 + i);
            ArmSiDmaCompletion(readToDram: false, dramAddr);
        }

        public void SI_STATUS_WRITE_EVENT()
        {
            // Any write to SI_STATUS clears the SI interrupt.
            uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R);
            siStatus &= ~SiStatusInterrupt;
            WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
            ClearMiSiInterrupt();
        }

        public void PIF_RAM_WRITE_EVENT()
        {
            // Direct CPU writes to PIF RAM are not SI DMA transactions.
            // Keep the write visible immediately and only process control flags in-place.
            // Ignore emulator-owned boot/reset seeding before the CPU thread is live.
            if (!R4300.R4300_ON)
                return;

            if (_siDmaActive)
                return;

            ProcessPifControlFlags();

            if (TraceSiInterruptLifecycle || TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SIIRQ] direct-pif-write pc=0x{Registers.R4300.PC:x8} pifCtl=0x{PIFRAM[63]:x2} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} {BuildStoreContext()}");
            }
        }

        public void MI_INTR_MASK_WRITE_EVENT()
        {
            // MIPS Interface interrupt mask write semantics:
            // pairs of bits clear/set individual masks.
            uint value = ReadBigEndianWord(MI_INTR_MASK_REG_W);
            uint mask = ReadBigEndianWord(MI_INTR_MASK_REG_R) & 0x3Fu;
            if (TraceN64Io || (Registers.R4300.PC >= 0x80093000u && Registers.R4300.PC <= 0x80094500u))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] MI_INTR_MASK write value=0x{value:x8} oldMask=0x{mask:x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"pc=0x{Registers.R4300.PC:x8} cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} " +
                    $"cop0Cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8}");
            }

            ApplyMiMaskPair(ref mask, value, 0, 1, 0); // SP
            ApplyMiMaskPair(ref mask, value, 2, 3, 1); // SI
            ApplyMiMaskPair(ref mask, value, 4, 5, 2); // AI
            ApplyMiMaskPair(ref mask, value, 6, 7, 3); // VI
            ApplyMiMaskPair(ref mask, value, 8, 9, 4); // PI
            ApplyMiMaskPair(ref mask, value, 10, 11, 5); // DP

            WriteBigEndianWord(MI_INTR_MASK_REG_R, mask);
            RefreshCpuInterruptView();
            if (TraceN64Io || (Registers.R4300.PC >= 0x80093000u && Registers.R4300.PC <= 0x80094500u))
            {
                Common.Logger.PrintWarningLine($"[N64IO] MI_INTR_MASK new=0x{mask:x8}");
            }
        }

        public void MI_INIT_MODE_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(MI_INIT_MODE_REG_W);
            uint mode = ReadBigEndianWord(MI_INIT_MODE_REG_R) & 0x000003FFu;

            // MI init control semantics:
            // bits 0..6 write init length directly,
            // bit 7 clears init mode, bit 8 sets init mode,
            // bit 9 clears EBUS test mode, bit 10 sets EBUS test mode,
            // bit 11 clears DP interrupt,
            // bit 12 clears RDRAM reg mode, bit 13 sets RDRAM reg mode.
            mode &= ~0x0000007Fu;
            mode |= value & 0x0000007Fu;

            if ((value & 0x00000080u) != 0) mode &= ~0x00000080u;
            if ((value & 0x00000100u) != 0) mode |= 0x00000080u;

            if ((value & 0x00000200u) != 0) mode &= ~0x00000100u;
            if ((value & 0x00000400u) != 0) mode |= 0x00000100u;

            if ((value & 0x00000800u) != 0)
                ClearMiDpInterrupt();

            if ((value & 0x00001000u) != 0) mode &= ~0x00000200u;
            if ((value & 0x00002000u) != 0) mode |= 0x00000200u;

            WriteBigEndianWord(MI_INIT_MODE_REG_R, mode);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] MI_INIT_MODE write value=0x{value:x8} new=0x{mode:x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        public void SP_RD_LEN_WRITE_EVENT()
        {
            if (TraceN64Io)
            {
                uint len = ReadBigEndianWord(SP_RD_LEN_REG_RW);
                uint mem = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
                uint dram = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP_RD_LEN write len=0x{len:x8} spMem=0x{mem:x8} dram=0x{dram:x8} pc=0x{Registers.R4300.PC:x8}");
            }
            ExecuteSpDma(isReadFromDram: true);
        }

        public void SP_MEM_ADDR_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
            bool traceLateSpRegs =
                IsTraceN64SpDmaEnabled() &&
                Registers.R4300.PC >= 0x800A0000u && Registers.R4300.PC <= 0x800A2000u;

            if (!TraceN64Io && !traceLateSpRegs)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64SPREG] SP_MEM_ADDR write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        public void SP_DRAM_ADDR_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
            bool suspiciousLowDram = (value & 0x00FFFFFFu) < 0x00001000u;
            bool traceLateSpRegs =
                IsTraceN64SpDmaEnabled() &&
                (suspiciousLowDram || (Registers.R4300.PC >= 0x800A0000u && Registers.R4300.PC <= 0x800A2000u));

            // Always log SP_DRAM_ADDR writes when debugging Mega Man 64
            bool forceTraceSpDram = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SP_DRAM"), "1", StringComparison.Ordinal);
            
            if (!TraceN64Io && !traceLateSpRegs && !forceTraceSpDram)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64SPREG] SP_DRAM_ADDR write value=0x{value:x8} suspiciousLow={suspiciousLowDram} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        public void SP_SEMAPHORE_READ_EVENT()
        {
            WriteBigEndianWord(SP_SEMAPHORE_REG_R, 1);
        }

        public void SP_PC_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(SP_PC_REG_RW) & 0x00000FFCu;
            WriteBigEndianWord(SP_PC_REG_RW, value);
            if (!TraceN64Io)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64IO] SP_PC write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        public void SP_WR_LEN_WRITE_EVENT()
        {
            uint len = ReadBigEndianWord(SP_WR_LEN_REG_RW);
            uint mem = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
            uint dram = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
            bool suspiciousLowDram = (dram & 0x00FFFFFFu) < 0x00001000u;
            bool traceLateSpRegs =
                IsTraceN64SpDmaEnabled() &&
                (suspiciousLowDram || (Registers.R4300.PC >= 0x800A0000u && Registers.R4300.PC <= 0x800A2000u));

            if (TraceN64Io || traceLateSpRegs)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPREG] SP_WR_LEN write len=0x{len:x8} spMem=0x{mem:x8} dram=0x{dram:x8} suspiciousLow={suspiciousLowDram} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }
            ExecuteSpDma(isReadFromDram: false);
        }

        private void ExecuteSpDma(bool isReadFromDram)
        {
            SpDmaRequest request = new SpDmaRequest
            {
                IsReadFromDram = isReadFromDram,
                MemAddr = ReadBigEndianWord(SP_MEM_ADDR_REG_RW),
                DramAddr = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW),
                LengthReg = ReadBigEndianWord(isReadFromDram ? SP_RD_LEN_REG_RW : SP_WR_LEN_REG_RW)
            };

            if (_spDmaBusy)
            {
                if (!_spQueuedDmaValid)
                {
                    _spQueuedDma = request;
                    _spQueuedDmaValid = true;
                    _spDmaFull = true;
                    WriteBigEndianWord(SP_DMA_FULL_REG_R, 1);
                    uint fullStatus = ReadBigEndianWord(SP_STATUS_REG_R) | 0x00000008u;
                    WriteBigEndianWord(SP_STATUS_REG_R, fullStatus);

                    if (IsTraceN64SpDmaEnabled())
                    {
                        Common.Logger.PrintWarningLine(
                            $"[N64SPDMA] queue pc=0x{Registers.R4300.PC:x8} " +
                            $"read={request.IsReadFromDram} mem=0x{request.MemAddr:x8} dram=0x{request.DramAddr:x8} len=0x{request.LengthReg:x8}");
                    }
                }
                else if (IsTraceN64SpDmaEnabled())
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64SPDMA] queue-drop pc=0x{Registers.R4300.PC:x8} " +
                        $"read={request.IsReadFromDram} mem=0x{request.MemAddr:x8} dram=0x{request.DramAddr:x8} len=0x{request.LengthReg:x8} " +
                        $"queuedMem=0x{_spQueuedDma.MemAddr:x8} queuedDram=0x{_spQueuedDma.DramAddr:x8} queuedLen=0x{_spQueuedDma.LengthReg:x8}");
                }

                return;
            }

            ExecuteSpDma(request);
        }

        private void ExecuteSpDma(SpDmaRequest request)
        {
            bool isReadFromDram = request.IsReadFromDram;
            uint memBank = request.MemAddr & 0x1000u;
            uint memAddr = request.MemAddr & 0x0FF8u;
            uint startMemAddr = memBank | memAddr;
            uint dramAddr = request.DramAddr & 0x00FFFFF8u;
            uint lenReg = request.LengthReg;
            uint startDramAddr = dramAddr;
            uint rdramSize = (uint)RDRAM.Length;

	            int transferLength = (int)(((lenReg & 0xFFFu) | 7u) + 1u);
	            int count = (int)(((lenReg >> 12) & 0xFFu) + 1u);
	            int skip = (int)((lenReg >> 20) & 0xFFFu);
	            const uint lowRamProtectEnd = 0x00000400u;

	            if (transferLength <= 0 || count <= 0)
	                return;

            // Bring-up guard: reject any RSP write-DMA touching low RDRAM. The current
            // bad path wipes the boot/exception area and leads to the late 0x80000300
            // trap once the vectors are gone.
	            if (!isReadFromDram)
	            {
	                uint checkMemAddr = memAddr;
	                uint checkDramAddr = dramAddr;
                for (int block = 0; block < count; block++)
                {
                    uint blockStart = checkDramAddr & 0x00FFFFFFu;
                    uint blockEnd = (blockStart + (uint)transferLength) & 0x00FFFFFFu;
                    if (blockStart < lowRamProtectEnd)
                    {
                        Common.Logger.PrintWarningLine(
                            $"[N64SPDMA] protected low RAM window in write-DMA pc=0x{Registers.R4300.PC:x8} " +
                            $"block={block} dram=0x{blockStart:x8}->0x{blockEnd:x8} spMem=0x{(memBank | checkMemAddr):x8} " +
                            $"lenReg=0x{lenReg:x8} transferLength=0x{transferLength:x} count=0x{count:x} skip=0x{skip:x}");
                        break;
                    }

                    checkMemAddr = (checkMemAddr + (uint)transferLength) & 0x0FFFu;
                    checkDramAddr = (checkDramAddr + (uint)(transferLength + skip)) & 0x00FFFFFFu;
                }
            }

            bool traceSpDma = IsTraceN64SpDmaEnabled();
            bool traceDetailedSpWriteDma = traceSpDma && !isReadFromDram;

            if (traceSpDma)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPDMA] start pc=0x{Registers.R4300.PC:x8} " +
                    $"read={isReadFromDram} " +
                    $"spMem=0x{request.MemAddr:x8}->0x{memAddr:x8} dram=0x{request.DramAddr:x8}->0x{dramAddr:x8} " +
                    $"lenReg=0x{lenReg:x8} transferLength=0x{transferLength:x} count=0x{count:x} skip=0x{skip:x}");
            }

            _spDmaBusy = true;
            WriteBigEndianWord(SP_DMA_BUSY_REG_R, 1);
            uint status = ReadBigEndianWord(SP_STATUS_REG_R) | SpStatusDmaBusy;
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            bool skippedProtectedLowRamWrite = false;
            bool skippedOutOfRangeRdram = false;

            for (int block = 0; block < count; block++)
            {
                for (int i = 0; i < transferLength; i++)
                {
                    uint spAddress = memBank | ((memAddr + (uint)i) & 0x0FFFu);
                    uint rawRdAddress = dramAddr + (uint)i;
                    bool rdAddressInRange = rawRdAddress < rdramSize;
                    uint rdAddress = rawRdAddress & 0x007FFFFFu;

                    if (isReadFromDram)
                    {
                        byte value = 0;
                        if (rdAddressInRange)
                        {
                            value = ReadUInt8(PhysicalToKseg1(rdAddress));
                        }
                        else
                        {
                            if (!skippedOutOfRangeRdram && (traceSpDma || TraceRspTaskDmem))
                            {
                                Common.Logger.PrintWarningLine(
                                    $"[N64SPDMA] SKIP out-of-range RDRAM byte in read-DMA pc=0x{Registers.R4300.PC:x8} " +
                                    $"block={block} i=0x{i:x} rawRdAddr=0x{rawRdAddress:x8} spAddr=0x{spAddress:x4} " +
                                    $"lenReg=0x{lenReg:x8}");
                            }
                            skippedOutOfRangeRdram = true;
                        }
                        WriteSpMemoryByte(spAddress, value);
                    }
	                    else
	                    {
	                        byte value = ReadSpMemoryByte(spAddress);
	                        if (!rdAddressInRange)
	                        {
	                            if (!skippedOutOfRangeRdram && (traceSpDma || TraceRspTaskDmem))
	                            {
	                                Common.Logger.PrintWarningLine(
	                                    $"[N64SPDMA] SKIP out-of-range RDRAM byte in write-DMA pc=0x{Registers.R4300.PC:x8} " +
	                                    $"block={block} i=0x{i:x} rawRdAddr=0x{rawRdAddress:x8} spAddr=0x{spAddress:x4} value=0x{value:x2} " +
	                                    $"lenReg=0x{lenReg:x8}");
	                            }
	                            skippedOutOfRangeRdram = true;
	                            continue;
	                        }
	                        if (rdAddress < lowRamProtectEnd)
	                        {
	                            if (!skippedProtectedLowRamWrite)
	                            {
	                                Common.Logger.PrintWarningLine(
	                                    $"[N64SPDMA] SKIP protected low RAM byte in write-DMA pc=0x{Registers.R4300.PC:x8} " +
	                                    $"block={block} i=0x{i:x} rdAddr=0x{rdAddress:x8} spAddr=0x{spAddress:x4} value=0x{value:x2} " +
	                                    $"lenReg=0x{lenReg:x8}");
	                            }
	                            skippedProtectedLowRamWrite = true;
	                            continue;
	                        }
	                        if (traceDetailedSpWriteDma && (block == 0 || rdAddress < 0x400u))
	                        {
	                            Common.Logger.PrintWarningLine(
                                $"[N64SPDMA] write block={block} i=0x{i:x} spAddr=0x{spAddress:x4} rdAddr=0x{rdAddress:x8} value=0x{value:x2} " +
                                $"pc=0x{Registers.R4300.PC:x8}");
                        }
                        WithWriteUInt8Origin("sp-dma-write", () =>
                        {
                            WriteUInt8(PhysicalToKseg1(rdAddress), value);
                        });
                    }
                }

                memAddr = (memAddr + (uint)transferLength) & 0x0FFFu;
                dramAddr = (dramAddr + (uint)(transferLength + skip)) & 0x00FFFFFFu;
            }

            if (traceSpDma)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPDMA] end pc=0x{Registers.R4300.PC:x8} " +
                    $"read={isReadFromDram} " +
                    $"startMem=0x{startMemAddr:x4} endMem=0x{memAddr:x4} startDram=0x{startDramAddr:x8} endDram=0x{dramAddr:x8} " +
                    $"skippedProtectedLowRam={skippedProtectedLowRamWrite} skippedOutOfRangeRdram={skippedOutOfRangeRdram}");
            }

            WriteBigEndianWord(SP_MEM_ADDR_REG_RW, memAddr & 0x0FFFu);
            WriteBigEndianWord(SP_DRAM_ADDR_REG_RW, dramAddr & 0x00FFFFFFu);

            if (isReadFromDram && (TraceN64Io || TraceRspTaskDmem))
            {
                TraceRspDmemWindowAfterReadDma(startMemAddr, request.DramAddr & 0x00FFFFF8u, transferLength, count, skip);
                TraceRspImemWindowAfterDma(startMemAddr, request.DramAddr & 0x00FFFFF8u, transferLength, count, skip);
            }

            if (isReadFromDram)
                WriteBigEndianWord(SP_RD_LEN_REG_RW, 0x00000FF8u);
            else
                WriteBigEndianWord(SP_WR_LEN_REG_RW, 0x00000FF8u);

            _spDmaDelayArmed = true;
            _spDmaDelayRemaining = Math.Max(1u, (uint)((count * transferLength) / 8));
        }

        private byte ReadSpMemoryByte(uint spAddress)
        {
            if ((spAddress & 0x1000u) != 0)
                return SP_IMEM_RW[spAddress & 0x0FFFu];
            return SP_DMEM_RW[spAddress & 0x0FFFu];
        }

        private void WriteSpMemoryByte(uint spAddress, byte value)
        {
            if ((spAddress & 0x1000u) != 0)
                SP_IMEM_RW[spAddress & 0x0FFFu] = value;
            else
                SP_DMEM_RW[spAddress & 0x0FFFu] = value;
        }

        public void SP_STATUS_WRITE_EVENT()
        {
            uint writeValue = ReadBigEndianWord(SP_STATUS_REG_W);
            uint status = ReadBigEndianWord(SP_STATUS_REG_R);
            bool rspEventPending = _rspInterruptDelayArmed;
            if (TraceN64Io)
            {
                string storeCtx = BuildStoreContext();
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP_STATUS write value=0x{writeValue:x8} old=0x{status:x8} pc=0x{Registers.R4300.PC:x8} {storeCtx}");
            }

            // SP_STATUS write control bits use set/clear pairs.
            bool clearHalt = (writeValue & 0x00000003u) == 0x00000001u;
            bool setHalt = (writeValue & 0x00000003u) == 0x00000002u;
            bool clearBroke = (writeValue & 0x00000004u) != 0;
            if (clearHalt) status &= ~SpStatusHalt;
            if (setHalt) status |= SpStatusHalt;
            if (clearBroke) status &= ~SpStatusBroke;
            if ((writeValue & 0x00000008u) != 0) ClearMiSpInterrupt();    // CLR_INTR
            if ((writeValue & 0x00000010u) != 0) SetMiSpInterrupt();      // SET_INTR
            if ((writeValue & 0x00000020u) != 0) status &= ~0x00000020u; // CLR_SSTEP
            if ((writeValue & 0x00000040u) != 0) status |= 0x00000020u;  // SET_SSTEP
            if ((writeValue & 0x00000080u) != 0) status &= ~SpStatusIntrBreak; // CLR_INTR_BREAK
            if ((writeValue & 0x00000100u) != 0) status |= SpStatusIntrBreak;  // SET_INTR_BREAK
            if ((writeValue & 0x00000200u) != 0) status &= ~0x00000080u; // CLR_SIG0
            if ((writeValue & 0x00000400u) != 0) status |= 0x00000080u;  // SET_SIG0
            if ((writeValue & 0x00000800u) != 0) status &= ~0x00000100u; // CLR_SIG1
            if ((writeValue & 0x00001000u) != 0) status |= 0x00000100u;  // SET_SIG1
            if ((writeValue & 0x00002000u) != 0) status &= ~0x00000200u; // CLR_SIG2
            if ((writeValue & 0x00004000u) != 0) status |= 0x00000200u;  // SET_SIG2
            if ((writeValue & 0x00008000u) != 0) status &= ~0x00000400u; // CLR_SIG3
            if ((writeValue & 0x00010000u) != 0) status |= 0x00000400u;  // SET_SIG3
            if ((writeValue & 0x00020000u) != 0) status &= ~0x00000800u; // CLR_SIG4
            if ((writeValue & 0x00040000u) != 0) status |= 0x00000800u;  // SET_SIG4
            if ((writeValue & 0x00080000u) != 0) status &= ~0x00001000u; // CLR_SIG5
            if ((writeValue & 0x00100000u) != 0) status |= 0x00001000u;  // SET_SIG5
            if ((writeValue & 0x00200000u) != 0) status &= ~0x00002000u; // CLR_SIG6
            if ((writeValue & 0x00400000u) != 0) status |= 0x00002000u;  // SET_SIG6
            if ((writeValue & 0x00800000u) != 0) status &= ~0x00004000u; // CLR_SIG7
            if ((writeValue & 0x01000000u) != 0) status |= 0x00004000u;  // SET_SIG7

            if (_rspTaskLocked && rspEventPending)
            {
                WriteBigEndianWord(SP_STATUS_REG_R, status);
                if (TraceN64Io)
                    Common.Logger.PrintWarningLine($"[N64IO] SP_STATUS new=0x{status:x8} (task locked)");
                return;
            }

            bool rspShouldStart = !_rspTaskActive
                && !_rspTaskDispatching
                && (_rspTaskLocked || clearHalt || clearBroke)
                && (status & SpStatusHalt) == 0;

            if (TraceN64Io && (clearHalt || clearBroke || (writeValue & 0x00000118u) != 0))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP_STATUS gate clearHalt={clearHalt} setHalt={setHalt} clearBroke={clearBroke} " +
                    $"taskActive={_rspTaskActive} taskLocked={_rspTaskLocked} dispatching={_rspTaskDispatching} " +
                    $"rspEventPending={rspEventPending} shouldStart={rspShouldStart} " +
                    $"statusAfterCtrl=0x{status:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            if (rspShouldStart && (TraceN64Io || TraceRspTaskDmem))
                TraceRspTaskHeaderWords(0x0FC0u, "sp-kick");
            else if (TraceN64Io && (clearHalt || clearBroke))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP_STATUS no-start reason halt={(status & SpStatusHalt) != 0} " +
                    $"taskActive={_rspTaskActive} dispatching={_rspTaskDispatching} taskLocked={_rspTaskLocked} " +
                    $"pc=0x{Registers.R4300.PC:x8}");
            }
            if (rspShouldStart)
            {
                if (EnableRspInterpreter)
                    TryDispatchRspTaskInterpreter(ref status);
                else if (EnableRspTaskHleDispatcher)
                    TryDispatchRspTaskHle(ref status);
            }

            // Optional bring-up behavior: when CPU clears HALT to kick RSP, complete task immediately.
            // Disabled by default because it can distort scheduler/task-queue flow.
            if (AutoCompleteRspTaskOnHaltClear && rspShouldStart)
            {
                _rspKickCount++;
                if (!_warnedRspTaskStub)
                {
                    _warnedRspTaskStub = true;
                    Common.Logger.PrintWarningLine(
                        "[N64] RSP task execution is currently stubbed (HALT clear auto-completes task). " +
                        "3D graphics/audio tasks will not render correctly until real RSP/RDP emulation is implemented.");
                }
                else if (TraceN64Io && (_rspKickCount <= 8 || (_rspKickCount % 256) == 0))
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64IO] RSP task kick auto-completed (count={_rspKickCount}) pc=0x{Registers.R4300.PC:x8}");
                }

                status |= SpStatusHalt | SpStatusBroke;
                SetMiSpInterrupt();
            }

            WriteBigEndianWord(SP_STATUS_REG_R, status);
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine($"[N64IO] SP_STATUS new=0x{status:x8}");
            }
        }

        public void DPC_START_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(DPC_START_REG_RW);
            WriteBigEndianWord(DPC_START_REG_RW, value);
            WriteBigEndianWord(DPC_CURRENT_REG_RW, value);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] DPC_START write start=0x{value:x8} current=0x{ReadBigEndianWord(DPC_CURRENT_REG_RW):x8} " +
                    $"status=0x{ReadBigEndianWord(DPC_STATUS_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        public void DPC_END_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(DPC_END_REG_RW);
            WriteBigEndianWord(DPC_END_REG_RW, value);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] DPC_END queued start=0x{ReadBigEndianWord(DPC_START_REG_RW):x8} end=0x{value:x8} " +
                    $"status=0x{ReadBigEndianWord(DPC_STATUS_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        public void DPC_STATUS_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(DPC_STATUS_REG_W);
            uint status = ReadBigEndianWord(DPC_STATUS_REG_R);

            if ((value & DpcClrXbusDmemDma) != 0) status &= ~DpcStatusXbusDmemDma;
            if ((value & DpcSetXbusDmemDma) != 0) status |= DpcStatusXbusDmemDma;

            if ((value & DpcClrFreeze) != 0) status &= ~DpcStatusFreeze;
            if ((value & DpcSetFreeze) != 0) status |= DpcStatusFreeze;
            if ((value & DpcClrFlush) != 0) status &= ~DpcStatusFlush;
            if ((value & DpcSetFlush) != 0) status |= DpcStatusFlush;

            WriteBigEndianWord(DPC_STATUS_REG_R, status);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] DPC_STATUS write value=0x{value:x8} new=0x{status:x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private struct RspTask
        {
            public uint Type;
            public uint Flags;
            public uint Ucode;
            public uint UcodeSize;
            public uint UcodeData;
            public uint UcodeDataSize;
            public uint DataPtr;
            public uint DataSize;
            public uint YieldDataPtr;
            public uint YieldDataSize;
        }

        private struct SpDmaRequest
        {
            public bool IsReadFromDram;
            public uint MemAddr;
            public uint DramAddr;
            public uint LengthReg;
        }

        private void TryDispatchRspTaskHle(ref uint status)
        {
            if (!TryReadRspTaskFromDmem(out RspTask task))
                return;

            _rspKickCount++;

            if (!_warnedRspTaskHle)
            {
                _warnedRspTaskHle = true;
                Common.Logger.PrintWarningLine(
                    "[N64] RSP HLE dispatcher active: tasks are acknowledged/completed, " +
                    "but real RSP microcode execution is not implemented yet.");
            }

            if ((TraceN64Io || TraceRspTaskDmem) && (_rspKickCount <= 16 || (_rspKickCount % 256) == 0))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP task dispatch type={task.Type} flags=0x{task.Flags:x8} " +
                    $"ucode=0x{task.Ucode:x8}/0x{task.UcodeSize:x} " +
                    $"ucodeData=0x{task.UcodeData:x8}/0x{task.UcodeDataSize:x} " +
                    $"data=0x{task.DataPtr:x8}/0x{task.DataSize:x} " +
                    $"yield=0x{task.YieldDataPtr:x8}/0x{task.YieldDataSize:x} " +
                    $"pc=0x{Registers.R4300.PC:x8}");
            }

            _activeRspTask = task;
            _rspTaskActive = true;
            _rspTaskLocked = false;
            _rspTaskCyclesRemaining = GetRspExecutionCycles(task.Type);
            _rspInterruptDelayArmed = false;
            _dpInterruptDelayArmed = false;

            status &= ~(SpStatusHalt | SpStatusBroke | SpStatusTaskDone);
            ClearMiSpInterrupt();

        }

        private void TryDispatchRspTaskInterpreter(ref uint status)
        {
            if (!TryReadRspTaskFromDmem(out RspTask task))
                return;

            if (EnableRspInterpreterGraphicsOnly && task.Type != 1)
            {
                if (EnableRspTaskHleDispatcher)
                    TryDispatchRspTaskHle(ref status);
                return;
            }

            _rspKickCount++;
            _activeRspTask = task;

            if (TraceN64Io || TraceRspTaskDmem)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP interpreter dispatch type={task.Type} flags=0x{task.Flags:x8} " +
                    $"ucode=0x{task.Ucode:x8}/0x{task.UcodeSize:x} " +
                    $"ucodeData=0x{task.UcodeData:x8}/0x{task.UcodeDataSize:x} " +
                    $"data=0x{task.DataPtr:x8}/0x{task.DataSize:x} " +
                    $"yield=0x{task.YieldDataPtr:x8}/0x{task.YieldDataSize:x} " +
                    $"pc=0x{Registers.R4300.PC:x8}");
            }

            uint executedInstructions;
            string stopReason;
            bool completed;
            _rspTaskDispatching = true;
            try
            {
                completed = _rspInterpreter.ExecuteTask(out executedInstructions, out stopReason);
            }
            finally
            {
                _rspTaskDispatching = false;
            }

            if (!completed && string.IsNullOrEmpty(stopReason))
                stopReason = $"unknown-incomplete executed={executedInstructions} rspPc=0x{ReadRspPc():x3}";

            if (TraceN64Io || TraceRspTaskDmem)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP interpreter task type={task.Type} executed={executedInstructions} completed={completed} stop='{stopReason}' pc=0x{Registers.R4300.PC:x8}");
            }

            if (!completed)
            {
                if (EnableRspTaskHleDispatcher)
                {
                    if (!_warnedRspInterpreterFallback)
                    {
                        _warnedRspInterpreterFallback = true;
                        Common.Logger.PrintWarningLine(
                            $"[N64] RSP interpreter hit an unimplemented path ({stopReason}); falling back to task HLE.");
                    }

                    TryDispatchRspTaskHle(ref status);
                }

                return;
            }

            _rspTaskActive = true;
            _rspTaskLocked = false;
            _rspTaskCyclesRemaining = GetRspExecutionCycles(task.Type);
            _rspInterruptDelayArmed = false;
            _dpInterruptDelayArmed = false;

            status &= ~(SpStatusHalt | SpStatusBroke | SpStatusTaskDone);
            ClearMiSpInterrupt();
        }

        private bool TryReadRspTaskFromDmem(out RspTask task)
        {
            // OS schedules tasks by writing an OSTask at DMEM+0xFC0.
            const uint taskBase = 0x0FC0u;
            task = new RspTask
            {
                Type = ReadSpDmemWord(taskBase + 0x00),
                Flags = ReadSpDmemWord(taskBase + 0x04),
                Ucode = ReadSpDmemWord(taskBase + 0x10),
                UcodeSize = ReadSpDmemWord(taskBase + 0x14),
                UcodeData = ReadSpDmemWord(taskBase + 0x18),
                UcodeDataSize = ReadSpDmemWord(taskBase + 0x1C),
                DataPtr = ReadSpDmemWord(taskBase + 0x30),
                DataSize = ReadSpDmemWord(taskBase + 0x34),
                YieldDataPtr = ReadSpDmemWord(taskBase + 0x38),
                YieldDataSize = ReadSpDmemWord(taskBase + 0x3C),
            };

            if (task.Type == 0 || task.Type > 4)
            {
                if (TraceRspTaskDmem || TraceN64Io)
                    TraceRspTaskHeaderWords(taskBase, $"reject:type=0x{task.Type:x8}");
                return false;
            }

            // Keep validation intentionally permissive for bring-up.
            if (task.Ucode == 0 || task.DataPtr == 0)
            {
                if (TraceRspTaskDmem || TraceN64Io)
                    TraceRspTaskHeaderWords(taskBase, $"reject:ucode=0x{task.Ucode:x8} data=0x{task.DataPtr:x8}");
                return false;
            }

            return true;
        }

        private void TraceRspTaskHeaderWords(uint taskBase, string reason)
        {
            uint w0 = ReadSpDmemWord(taskBase + 0x00);
            uint w1 = ReadSpDmemWord(taskBase + 0x04);
            uint w2 = ReadSpDmemWord(taskBase + 0x08);
            uint w3 = ReadSpDmemWord(taskBase + 0x0C);
            uint w4 = ReadSpDmemWord(taskBase + 0x10);
            uint w5 = ReadSpDmemWord(taskBase + 0x14);
            uint w6 = ReadSpDmemWord(taskBase + 0x18);
            uint w7 = ReadSpDmemWord(taskBase + 0x1C);
            uint wC = ReadSpDmemWord(taskBase + 0x30);
            uint wD = ReadSpDmemWord(taskBase + 0x34);
            Common.Logger.PrintWarningLine(
                $"[N64IO] RSP task header dump ({reason}) " +
                $"w0=0x{w0:x8} w1=0x{w1:x8} w2=0x{w2:x8} w3=0x{w3:x8} w4=0x{w4:x8} w5=0x{w5:x8} w6=0x{w6:x8} w7=0x{w7:x8} " +
                $"wC=0x{wC:x8} wD=0x{wD:x8} pc=0x{Registers.R4300.PC:x8}");
        }

        internal uint ReadSpDmemWord(uint dmemOffset)
        {
            uint index = dmemOffset & 0x0FFFu;
            return ((uint)SP_DMEM_RW[index] << 24)
                 | ((uint)SP_DMEM_RW[(index + 1) & 0x0FFFu] << 16)
                 | ((uint)SP_DMEM_RW[(index + 2) & 0x0FFFu] << 8)
                 | SP_DMEM_RW[(index + 3) & 0x0FFFu];
        }

        internal uint ReadSpImemWord(uint imemOffset)
        {
            uint index = imemOffset & 0x0FFFu;
            return ((uint)SP_IMEM_RW[index] << 24)
                 | ((uint)SP_IMEM_RW[(index + 1) & 0x0FFFu] << 16)
                 | ((uint)SP_IMEM_RW[(index + 2) & 0x0FFFu] << 8)
                 | SP_IMEM_RW[(index + 3) & 0x0FFFu];
        }

        private void TraceRspImemWindowAfterDma(uint startMemAddr, uint dramAddr, int transferLength, int count, int skip)
        {
            uint windowStart = 0x0B0u;
            uint windowEndInclusive = 0x0C8u;
            bool touchedImem = false;

            for (int block = 0; block < count && !touchedImem; block++)
            {
                uint blockStart = (startMemAddr + (uint)(block * transferLength)) & 0x1FFFu;
                uint blockEnd = blockStart + (uint)Math.Max(transferLength - 1, 0);
                if ((blockStart & 0x1000u) == 0 && (blockEnd & 0x1000u) == 0)
                    continue;

                for (int i = 0; i < transferLength; i++)
                {
                    uint spAddress = (blockStart + (uint)i) & 0x1FFFu;
                    if ((spAddress & 0x1000u) == 0)
                        continue;

                    uint imemOffset = spAddress & 0x0FFFu;
                    if (imemOffset >= windowStart && imemOffset <= windowEndInclusive)
                    {
                        touchedImem = true;
                        break;
                    }
                }
            }

            if (!touchedImem)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64IO] RSP IMEM DMA window startMem=0x{startMemAddr:x4} dram=0x{dramAddr:x8} len={transferLength} count={count} skip={skip} " +
                $"w0b0=0x{ReadSpImemWord(0x0B0):x8} w0b4=0x{ReadSpImemWord(0x0B4):x8} w0b8=0x{ReadSpImemWord(0x0B8):x8} " +
                $"w0bc=0x{ReadSpImemWord(0x0BC):x8} w0c0=0x{ReadSpImemWord(0x0C0):x8} w0c4=0x{ReadSpImemWord(0x0C4):x8} w0c8=0x{ReadSpImemWord(0x0C8):x8} " +
                $"pc=0x{Registers.R4300.PC:x8}");
        }

        private void TraceRspDmemWindowAfterReadDma(uint startMemAddr, uint dramAddr, int transferLength, int count, int skip)
        {
            if (_traceRspDmaWindowLogCount >= TraceRspDmaWindowLogLimit)
                return;

            if (startMemAddr == 0 && transferLength >= 0x800)
            {
                _traceRspDmaWindowLogCount++;
                Common.Logger.PrintWarningLine(
                    $"[N64RSPDMEMLOW] startMem=0x{startMemAddr:x4} dram=0x{(dramAddr & 0x00FFFFF8u):x8} len=0x{transferLength:x} count=0x{count:x} skip=0x{skip:x} " +
                    $"dmem0000=0x{ReadSpDmemWord(0x0000):x8} dmem0004=0x{ReadSpDmemWord(0x0004):x8} dmem0008=0x{ReadSpDmemWord(0x0008):x8} dmem000c=0x{ReadSpDmemWord(0x000c):x8} " +
                    $"dmem0010=0x{ReadSpDmemWord(0x0010):x8} dmem0014=0x{ReadSpDmemWord(0x0014):x8} dmem0018=0x{ReadSpDmemWord(0x0018):x8} dmem001c=0x{ReadSpDmemWord(0x001c):x8}");
                return;
            }

            const uint windowStart = 0x02B0u;
            const uint windowEndInclusive = 0x02D8u;
            bool touchedWindow = false;

            for (int block = 0; block < count && !touchedWindow; block++)
            {
                uint blockStart = (startMemAddr + (uint)(block * transferLength)) & 0x1FFFu;
                uint blockEnd = blockStart + (uint)Math.Max(transferLength - 1, 0);
                if (blockStart <= windowEndInclusive && blockEnd >= windowStart)
                    touchedWindow = true;
            }

            if (!touchedWindow)
                return;

            _traceRspDmaWindowLogCount++;
            uint src = dramAddr & 0x00FFFFF8u;
            Common.Logger.PrintWarningLine(
                $"[N64RSPDMEMDMA] startMem=0x{startMemAddr:x4} dram=0x{src:x8} len=0x{transferLength:x} count=0x{count:x} skip=0x{skip:x} " +
                $"srcBytes={FormatPhysicalByteSpan(src, 16)} " +
                $"srcW0=0x{ReadUInt32Physical(src + 0x00):x8} srcW4=0x{ReadUInt32Physical(src + 0x04):x8} srcW8=0x{ReadUInt32Physical(src + 0x08):x8} srcWc=0x{ReadUInt32Physical(src + 0x0c):x8} " +
                $"dmem2b0=0x{ReadSpDmemWord(0x02B0):x8} dmem2b4=0x{ReadSpDmemWord(0x02B4):x8} dmem2b8=0x{ReadSpDmemWord(0x02B8):x8} dmem2bc=0x{ReadSpDmemWord(0x02BC):x8}");
        }

        private string FormatPhysicalByteSpan(uint physicalAddress, int count)
        {
            if (count <= 0)
                return string.Empty;

            char[] chars = new char[(count * 3) - 1];
            int pos = 0;
            for (int i = 0; i < count; i++)
            {
                byte value = ReadUInt8PhysicalUncached((physicalAddress + (uint)i) & 0x007FFFFFu);
                chars[pos++] = GetHexChar(value >> 4);
                chars[pos++] = GetHexChar(value & 0x0F);
                if (i != count - 1)
                    chars[pos++] = ' ';
            }

            return new string(chars);
        }

        private static char GetHexChar(int nibble)
        {
            return (char)(nibble < 10 ? ('0' + nibble) : ('a' + (nibble - 10)));
        }

        internal void TickRspInterpreter(uint rspCycles)
        {
            if (rspCycles == 0)
                return;

            AdvanceRspDpLifecycle(rspCycles);
        }

        internal void WriteSpDmemWord(uint dmemOffset, uint value)
        {
            uint index = dmemOffset & 0x0FFFu;
            SP_DMEM_RW[index] = (byte)(value >> 24);
            SP_DMEM_RW[(index + 1) & 0x0FFFu] = (byte)(value >> 16);
            SP_DMEM_RW[(index + 2) & 0x0FFFu] = (byte)(value >> 8);
            SP_DMEM_RW[(index + 3) & 0x0FFFu] = (byte)value;
        }

        internal uint ReadRspPc()
        {
            return ReadBigEndianWord(SP_PC_REG_RW) & 0x0FFCu;
        }

        internal void WriteRspPc(uint value)
        {
            WriteBigEndianWord(SP_PC_REG_RW, value & 0x0FFCu);
        }

        internal uint ReadRspCp0(int reg)
        {
            switch (reg & 0x1F)
            {
                case 0: return ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
                case 1: return ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
                case 2: return ReadBigEndianWord(SP_RD_LEN_REG_RW);
                case 3: return ReadBigEndianWord(SP_WR_LEN_REG_RW);
                case 4: return ReadBigEndianWord(SP_STATUS_REG_R);
                case 5: return ReadBigEndianWord(SP_DMA_FULL_REG_R);
                case 6: return ReadBigEndianWord(SP_DMA_BUSY_REG_R);
                case 7:
                    {
                        uint value = ReadBigEndianWord(SP_SEMAPHORE_REG_R);
                        WriteBigEndianWord(SP_SEMAPHORE_REG_R, 1);
                        return value;
                    }
                case 8: return ReadBigEndianWord(DPC_START_REG_RW);
                case 9: return ReadBigEndianWord(DPC_END_REG_RW);
                case 10: return ReadBigEndianWord(DPC_CURRENT_REG_RW);
                case 11: return ReadBigEndianWord(DPC_STATUS_REG_R);
                case 12: return ReadBigEndianWord(DPC_CLOCK_REG_RW);
                case 13: return ReadBigEndianWord(DPC_BUFBUSY_REG_RW);
                case 14: return ReadBigEndianWord(DPC_PIPEBUSY_REG_RW);
                case 15: return ReadBigEndianWord(DPC_TMEM_REG_RW);
                default: return 0;
            }
        }

        internal void WriteRspCp0(int reg, uint value)
        {
            switch (reg & 0x1F)
            {
                case 0:
                    WriteBigEndianWord(SP_MEM_ADDR_REG_RW, value);
                    TraceRspCp0Write(reg, value);
                    break;
                case 1:
                    uint translatedValue = value;
                    if (TryTranslateRspSegmentedDramAddress(value, out uint resolvedValue, out string translationReason))
                    {
                        translatedValue = resolvedValue;
                        Common.Logger.PrintWarningLine(
                            $"[N64RSPSEG] SP_DRAM_ADDR raw=0x{value:x8} -> phys=0x{translatedValue:x8} " +
                            $"reason={translationReason} taskType={_activeRspTask.Type} dataPtr=0x{_activeRspTask.DataPtr:x8} cpuPc=0x{Registers.R4300.PC:x8}");
                    }

                    WriteBigEndianWord(SP_DRAM_ADDR_REG_RW, translatedValue);
                    TraceRspCp0Write(reg, value);
                    break;
                case 2:
                    WriteBigEndianWord(SP_RD_LEN_REG_RW, value);
                    TraceRspCp0Write(reg, value);
                    SP_RD_LEN_WRITE_EVENT();
                    break;
                case 3:
                    WriteBigEndianWord(SP_WR_LEN_REG_RW, value);
                    TraceRspCp0Write(reg, value);
                    SP_WR_LEN_WRITE_EVENT();
                    break;
                case 4:
                    WriteBigEndianWord(SP_STATUS_REG_W, value);
                    TraceRspCp0Write(reg, value);
                    SP_STATUS_WRITE_EVENT();
                    break;
                case 7:
                    WriteBigEndianWord(SP_SEMAPHORE_REG_W, value);
                    WriteBigEndianWord(SP_SEMAPHORE_REG_R, 0);
                    break;
                case 8:
                    WriteBigEndianWord(DPC_START_REG_RW, value);
                    DPC_START_WRITE_EVENT();
                    break;
                case 9:
                    WriteBigEndianWord(DPC_END_REG_RW, value);
                    DPC_END_WRITE_EVENT();
                    break;
                case 11:
                    WriteBigEndianWord(DPC_STATUS_REG_W, value);
                    DPC_STATUS_WRITE_EVENT();
                    break;
            }
        }

        private bool TryTranslateRspSegmentedDramAddress(uint value, out uint translatedValue, out string reason)
        {
            translatedValue = value;
            reason = string.Empty;

            if (_activeRspTask.Type != 2 || _activeRspTask.DataPtr < 0x300u)
                return false;

            uint segment = value >> 24;
            uint offset = value & 0x00FFFFFFu;
            if (segment != 0x08u)
                return false;

            // Mega Man 64's first graphics task hands segment 8 offset 0x300 back to the task
            // data block we initially DMA from DataPtr. Without this, the interpreter truncates
            // the segmented address to low RDRAM and loops on zeroed commands.
            translatedValue = (_activeRspTask.DataPtr - 0x300u) + offset;
            reason = "seg8-dataPtr-minus-0x300";
            return true;
        }

        private static uint ReadBigEndianWord(byte[] arr)
        {
            if (arr == null || arr.Length < 4)
                return 0;

            return ((uint)arr[0] << 24)
                 | ((uint)arr[1] << 16)
                 | ((uint)arr[2] << 8)
                 | arr[3];
        }

        private static void WriteBigEndianWord(byte[] arr, uint value)
        {
            if (arr == null || arr.Length < 4)
                return;

            arr[0] = (byte)((value >> 24) & 0xFF);
            arr[1] = (byte)((value >> 16) & 0xFF);
            arr[2] = (byte)((value >> 8) & 0xFF);
            arr[3] = (byte)(value & 0xFF);
        }

        private static void ApplyMiMaskPair(ref uint mask, uint value, int clearBit, int setBit, int targetBit)
        {
            uint targetMask = 1u << targetBit;
            if ((value & (1u << clearBit)) != 0)
                mask &= ~targetMask;
            if ((value & (1u << setBit)) != 0)
                mask |= targetMask;
        }

        private void SetMiSpInterrupt(bool immediate = false)
        {
            const byte MiSpIntrBit = 0x01; // MI_INTR_REG bit for SP
            bool wasSet = (MI_INTR_REG_R[3] & MiSpIntrBit) != 0;
            MI_INTR_REG_R[3] |= MiSpIntrBit;
            if (immediate)
                RaiseCpuInterruptFromRcpEvent();
            else
                RefreshCpuInterruptView();
            if (TraceN64Io && !wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP interrupt raised miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void ClearMiSpInterrupt()
        {
            const byte MiSpIntrBit = 0x01; // MI_INTR_REG bit for SP
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiSpIntrBit);
            RefreshCpuInterruptView();
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP interrupt cleared miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void SetMiPiInterrupt(bool immediate = false)
        {
            const byte MiPiIntrBit = 0x10; // MI_INTR_REG bit for PI
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] raise-pi pc=0x{Registers.R4300.PC:x8} immediate={immediate} " +
                    $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8} " +
                    $"piStatusBefore=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntrBefore=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
            bool wasSet = (MI_INTR_REG_R[3] & MiPiIntrBit) != 0;
            MI_INTR_REG_R[3] |= MiPiIntrBit;

            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            piStatus |= PiStatusInterrupt;
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);
            _piIrqRaiseCount++;
            if (immediate)
                RaiseCpuInterruptFromRcpEvent();
            else
                RefreshCpuInterruptView();

            if (TracePiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIIRQ] raise#{_piIrqRaiseCount} pc=0x{Registers.R4300.PC:x8} immediate={immediate} alreadySet={wasSet} " +
                    $"status=0x{piStatus:x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
            }

            if (TraceN64Io && !wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI interrupt raised dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} " +
                    $"cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8} status=0x{piStatus:x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void ClearMiPiInterrupt()
        {
            const byte MiPiIntrBit = 0x10; // MI_INTR_REG bit for PI
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] clear-pi pc=0x{Registers.R4300.PC:x8} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntrBefore=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }
            bool wasSet = (MI_INTR_REG_R[3] & MiPiIntrBit) != 0;
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiPiIntrBit);
            RefreshCpuInterruptView();

            if (TracePiInterruptLifecycle && wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIIRQ] clear pc=0x{Registers.R4300.PC:x8} " +
                    $"status=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"dram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} cart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
            }

            if (TraceN64Io && wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] PI interrupt cleared status=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} " +
                    $"pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }
        }

        private void SetMiSiInterrupt(bool immediate = false)
        {
            const byte MiSiIntrBit = 0x02; // MI_INTR_REG bit for SI
            bool wasSet = (MI_INTR_REG_R[3] & MiSiIntrBit) != 0;
            MI_INTR_REG_R[3] |= MiSiIntrBit;

            uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R);
            siStatus |= SiStatusInterrupt;
            WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
            if (immediate)
                RaiseCpuInterruptFromRcpEvent();
            else
                RefreshCpuInterruptView();

            if (TraceSiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SIIRQ] raise pc=0x{Registers.R4300.PC:x8} immediate={immediate} alreadySet={wasSet} " +
                    $"siStatus=0x{siStatus:x8} pifCtl=0x{PIFRAM[63]:x2} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            if (TraceN64Io && !wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SI interrupt raised status=0x{siStatus:x8} pifCtl=0x{PIFRAM[63]:x2} " +
                    $"pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void ClearMiSiInterrupt()
        {
            const byte MiSiIntrBit = 0x02; // MI_INTR_REG bit for SI
            bool wasSet = (MI_INTR_REG_R[3] & MiSiIntrBit) != 0;
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiSiIntrBit);
            RefreshCpuInterruptView();

            if (TraceSiInterruptLifecycle && wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SIIRQ] clear pc=0x{Registers.R4300.PC:x8} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} pifCtl=0x{PIFRAM[63]:x2} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            if (TraceN64Io && wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SI interrupt cleared status=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} pifCtl=0x{PIFRAM[63]:x2} " +
                    $"pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }
        }

        private void SetMiViInterrupt(bool immediate = false)
        {
            const byte MiViIntrBit = 0x08; // MI_INTR_REG bit for VI
            bool wasSet = (MI_INTR_REG_R[3] & MiViIntrBit) != 0;
            MI_INTR_REG_R[3] |= MiViIntrBit;
            if (immediate)
                RaiseCpuInterruptFromRcpEvent();
            else
                RefreshCpuInterruptView();

            if (TraceViInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64VIIRQ] raise pc=0x{Registers.R4300.PC:x8} immediate={immediate} alreadySet={wasSet} " +
                    $"current={_viCurrentLine} intr={(ReadBigEndianWord(VI_INTR_REG_RW) & 0x03FFu)} " +
                    $"vsync={(ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu)} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            if (TraceN64Io && !wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] VI interrupt raised current={_viCurrentLine} intr={(ReadBigEndianWord(VI_INTR_REG_RW) & 0x03FFu)} " +
                    $"vsync={(ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu)} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void ClearMiViInterrupt()
        {
            const byte MiViIntrBit = 0x08; // MI_INTR_REG bit for VI
            bool wasSet = (MI_INTR_REG_R[3] & MiViIntrBit) != 0;
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiViIntrBit);
            RefreshCpuInterruptView();

            if (TraceViInterruptLifecycle && wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64VIIRQ] clear pc=0x{Registers.R4300.PC:x8} current={_viCurrentLine} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            if (TraceN64Io && wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] VI interrupt cleared current={_viCurrentLine} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }
        }

        private void SetMiAiInterrupt()
        {
            const byte MiAiIntrBit = 0x04; // MI_INTR_REG bit for AI
            MI_INTR_REG_R[3] |= MiAiIntrBit;
            RefreshCpuInterruptView();
        }

        private void ClearMiAiInterrupt()
        {
            const byte MiAiIntrBit = 0x04; // MI_INTR_REG bit for AI
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiAiIntrBit);
            RefreshCpuInterruptView();
        }

        private void SetMiDpInterrupt(bool immediate = false)
        {
            const byte MiDpIntrBit = 0x20; // MI_INTR_REG bit for DP
            bool wasSet = (MI_INTR_REG_R[3] & MiDpIntrBit) != 0;
            MI_INTR_REG_R[3] |= MiDpIntrBit;
            if (immediate)
                RaiseCpuInterruptFromRcpEvent();
            else
                RefreshCpuInterruptView();
            if (TraceN64Io && !wasSet)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] DP interrupt raised miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void ClearMiDpInterrupt()
        {
            const byte MiDpIntrBit = 0x20; // MI_INTR_REG bit for DP
            MI_INTR_REG_R[3] = (byte)(MI_INTR_REG_R[3] & ~MiDpIntrBit);
            RefreshCpuInterruptView();
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] DP interrupt cleared miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} " +
                    $"miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        public void VI_CURRENT_WRITE_EVENT()
        {
            if (TraceMegaStatusBlock || TraceMegaCallbackBlock || TraceMegaFatalBlock
                || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_MEGA_LATE_WINDOW"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64MEGALATEIO] VI_CURRENT ack pc=0x{Registers.R4300.PC:x8} current=0x{ReadBigEndianWord(VI_CURRENT_REG_RW):x8} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8}");
            }

            // VI_CURRENT is write-to-ack, not a normal writable latch.
            ClearMiViInterrupt();
        }

        public void VI_CURRENT_READ_EVENT()
        {
            RefreshViCurrentRegister();
        }

        public void VI_STATUS_WRITE_EVENT()
        {
            if (!TraceN64Io)
                return;

            uint value = ReadBigEndianWord(VI_STATUS_REG_RW);
            string storeCtx = BuildStoreContext();
            ulong a0 = Registers.R4300.Reg[4];
            ulong a1 = Registers.R4300.Reg[5];
            ulong a2 = Registers.R4300.Reg[6];
            ulong a3 = Registers.R4300.Reg[7];
            ulong v0 = Registers.R4300.Reg[2];
            ulong v1 = Registers.R4300.Reg[3];
            Common.Logger.PrintWarningLine(
                $"[N64IO] VI_STATUS write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} " +
                $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} v0=0x{v0:x16} v1=0x{v1:x16} {storeCtx}");
        }

        public void VI_INTR_WRITE_EVENT()
        {
            uint viIntr = ReadBigEndianWord(VI_INTR_REG_RW) & 0x03FFu;
            RecomputeViInterruptSchedule();

            if (!TraceN64Io)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64IO] VI_INTR write value=0x{viIntr:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        public void VI_V_SYNC_WRITE_EVENT()
        {
            uint viVSync = ReadBigEndianWord(VI_V_SYNC_REG_RW) & 0x03FFu;
            if (viVSync == 0)
            {
                _viFrameDelayCycles = 0;
                _viInterruptCyclesRemaining = 0;
                _viCurrentLine = 0;
                _viLineCycleAccum = 0;
                _viField = 0;
                WriteBigEndianWord(VI_CURRENT_REG_RW, 0u);
            }
            else
            {
                RecomputeViInterruptSchedule();
            }

            if (!TraceN64Io)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64IO] VI_V_SYNC write value=0x{viVSync:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        public void VI_BURST_WRITE_EVENT()
        {
            TraceViRegisterWrite("burst", ReadBigEndianWord(VI_BURST_REG_RW));
        }

        public void VI_ORIGIN_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(VI_ORIGIN_REG_RW) & 0x00FFFFFFu;
            _lastViOriginWriteValue = value;
            _lastViOriginWritePc = Registers.R4300.PC;

            bool plausible =
                value >= PlausibleFramebufferOriginFloor &&
                value < 0x00800000u &&
                (value & 0x7u) == 0;
            if (plausible)
            {
                _lastPlausibleViOriginWriteValue = value;
                _lastPlausibleViOriginWritePc = Registers.R4300.PC;
            }

            if (TraceViRegisterLifecycle)
            {
                string detail = !plausible ? $" {BuildDetailedStoreContext()} {BuildInstructionWindow()} {BuildReturnContext()}" : string.Empty;
                Common.Logger.PrintWarningLine(
                    $"[N64VIREG] origin-write value=0x{value:x8} plausible={plausible} pc=0x{Registers.R4300.PC:x8} " +
                    $"width=0x{ReadBigEndianWord(VI_WIDTH_REG_RW):x8} status=0x{ReadBigEndianWord(VI_STATUS_REG_RW):x8} " +
                    $"vStart=0x{ReadBigEndianWord(VI_V_START_REG_RW):x8}{detail}");
            }

            if (!TraceN64Io)
                return;

            string storeCtx = BuildStoreContext();
            ulong a0 = Registers.R4300.Reg[4];
            ulong a1 = Registers.R4300.Reg[5];
            ulong a2 = Registers.R4300.Reg[6];
            ulong a3 = Registers.R4300.Reg[7];
            ulong t0 = Registers.R4300.Reg[8];
            ulong t1 = Registers.R4300.Reg[9];
            ulong v0 = Registers.R4300.Reg[2];
            ulong v1 = Registers.R4300.Reg[3];
            Common.Logger.PrintWarningLine(
                $"[N64IO] VI_ORIGIN write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} " +
                $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} t0=0x{t0:x16} t1=0x{t1:x16} v0=0x{v0:x16} v1=0x{v1:x16} {storeCtx}");
        }

        public void VI_WIDTH_WRITE_EVENT()
        {
            if (!TraceN64Io)
                return;

            uint value = ReadBigEndianWord(VI_WIDTH_REG_RW);
            string storeCtx = BuildStoreContext();
            ulong a0 = Registers.R4300.Reg[4];
            ulong a1 = Registers.R4300.Reg[5];
            ulong a2 = Registers.R4300.Reg[6];
            ulong a3 = Registers.R4300.Reg[7];
            ulong v0 = Registers.R4300.Reg[2];
            ulong v1 = Registers.R4300.Reg[3];
            Common.Logger.PrintWarningLine(
                $"[N64IO] VI_WIDTH write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} " +
                $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} a3=0x{a3:x16} v0=0x{v0:x16} v1=0x{v1:x16} {storeCtx}");
        }

        public void VI_H_START_WRITE_EVENT()
        {
            TraceViRegisterWrite("h-start", ReadBigEndianWord(VI_H_START_REG_RW));
        }

        public void VI_V_START_WRITE_EVENT()
        {
            TraceViRegisterWrite("v-start", ReadBigEndianWord(VI_V_START_REG_RW));
        }

        public void VI_X_SCALE_WRITE_EVENT()
        {
            TraceViRegisterWrite("x-scale", ReadBigEndianWord(VI_X_SCALE_REG_RW));
        }

        public void VI_Y_SCALE_WRITE_EVENT()
        {
            TraceViRegisterWrite("y-scale", ReadBigEndianWord(VI_Y_SCALE_REG_RW));
        }

        private static string BuildStoreContext()
        {
            try
            {
                uint pc = Registers.R4300.PC;
                uint op = R4300.memory.ReadUInt32(pc);
                uint opcode = op >> 26;
                if (opcode != 0x2B)
                    return $"op=0x{op:x8}";

                int rs = (int)((op >> 21) & 0x1F);
                int rt = (int)((op >> 16) & 0x1F);
                short imm = (short)(op & 0xFFFF);
                ulong rsVal = Registers.R4300.Reg[rs];
                ulong rtVal = Registers.R4300.Reg[rt];
                return $"op=0x{op:x8} sw rs=r{rs}=0x{rsVal:x16} rt=r{rt}=0x{rtVal:x16} imm={imm}";
            }
            catch
            {
                return "op=<unavailable>";
            }
        }

        private static string BuildDetailedStoreContext()
        {
            try
            {
                uint pc = Registers.R4300.PC;
                uint op = R4300.memory.ReadUInt32(pc);
                uint opcode = op >> 26;
                if (opcode != 0x2B)
                    return $"storeDetail(op=0x{op:x8})";

                int rs = (int)((op >> 21) & 0x1F);
                int rt = (int)((op >> 16) & 0x1F);
                short imm = (short)(op & 0xFFFF);
                ulong rsVal = Registers.R4300.Reg[rs];
                ulong rtVal = Registers.R4300.Reg[rt];
                ulong eff = rsVal + (ulong)(long)imm;
                uint effWord = 0;
                uint effWord4 = 0;
                try { effWord = R4300.memory.ReadUInt32((uint)eff); } catch { }
                try { effWord4 = R4300.memory.ReadUInt32((uint)eff + 4u); } catch { }
                return
                    $"storeDetail(rs=r{rs}=0x{rsVal:x16} rt=r{rt}=0x{rtVal:x16} imm={imm} eff=0x{eff:x16} " +
                    $"[eff]=0x{effWord:x8} [eff+4]=0x{effWord4:x8})";
            }
            catch
            {
                return "storeDetail(<unavailable>)";
            }
        }

        private static string BuildInstructionWindow()
        {
            try
            {
                uint pc = Registers.R4300.PC;
                uint prev4 = R4300.memory.ReadUInt32(pc - 16u);
                uint prev3 = R4300.memory.ReadUInt32(pc - 12u);
                uint prev2 = R4300.memory.ReadUInt32(pc - 8u);
                uint prev1 = R4300.memory.ReadUInt32(pc - 4u);
                uint cur = R4300.memory.ReadUInt32(pc);
                uint next = R4300.memory.ReadUInt32(pc + 4u);
                uint next2 = R4300.memory.ReadUInt32(pc + 8u);
                return
                    $"ops(prev4=0x{prev4:x8} prev3=0x{prev3:x8} prev2=0x{prev2:x8} prev1=0x{prev1:x8} " +
                    $"cur=0x{cur:x8} next=0x{next:x8} next2=0x{next2:x8})";
            }
            catch
            {
                return "ops(<unavailable>)";
            }
        }

        private static string BuildReturnContext()
        {
            try
            {
                uint ra = (uint)Registers.R4300.Reg[31];
                uint sp = (uint)Registers.R4300.Reg[29];
                uint sp24 = 0;
                uint sp38 = 0;
                uint sp44 = 0;
                try { sp24 = R4300.memory.ReadUInt32(sp + 0x24u); } catch { }
                try { sp38 = R4300.memory.ReadUInt32(sp + 0x38u); } catch { }
                try { sp44 = R4300.memory.ReadUInt32(sp + 0x44u); } catch { }
                if (ra == 0)
                    return $"ra=0x00000000 sp=0x{sp:x8} [sp+24]=0x{sp24:x8} [sp+38]=0x{sp38:x8} [sp+44]=0x{sp44:x8}";

                uint callPrev = R4300.memory.ReadUInt32(ra - 8u);
                uint callDelay = R4300.memory.ReadUInt32(ra - 4u);
                uint callNext = R4300.memory.ReadUInt32(ra);
                return
                    $"ra=0x{ra:x8} sp=0x{sp:x8} [sp+24]=0x{sp24:x8} [sp+38]=0x{sp38:x8} [sp+44]=0x{sp44:x8} " +
                    $"caller(prev=0x{callPrev:x8} delay=0x{callDelay:x8} next=0x{callNext:x8})";
            }
            catch
            {
                return "ra=<unavailable>";
            }
        }

        private void TraceViRegisterWrite(string name, uint value)
        {
            if (TraceViRegisterLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64VIREG] {name}-write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} " +
                    $"origin=0x{ReadBigEndianWord(VI_ORIGIN_REG_RW):x8} width=0x{ReadBigEndianWord(VI_WIDTH_REG_RW):x8} " +
                    $"status=0x{ReadBigEndianWord(VI_STATUS_REG_RW):x8} vStart=0x{ReadBigEndianWord(VI_V_START_REG_RW):x8} " +
                    $"{BuildStoreContext()}");
            }

            if (!TraceN64Io)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64IO] VI_{name} write value=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        internal void SetActiveRspTracePc(uint value)
        {
            _activeRspTracePc = value & 0x0FFCu;
            _hasActiveRspTracePc = true;
        }

        internal void ClearActiveRspTracePc()
        {
            _hasActiveRspTracePc = false;
        }

        private void TraceRspCp0Write(int reg, uint value)
        {
            if (!TraceN64Io && !IsTraceN64SpDmaEnabled())
                return;

            uint memAddr = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
            uint dramAddr = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
            uint rdLen = ReadBigEndianWord(SP_RD_LEN_REG_RW);
            uint wrLen = ReadBigEndianWord(SP_WR_LEN_REG_RW);
            uint spStatus = ReadBigEndianWord(SP_STATUS_REG_R);
            bool suspiciousLowDram = (dramAddr & 0x00FFFFFFu) < 0x00001000u;
            string regName;
            switch (reg & 0x1F)
            {
                case 0: regName = "SP_MEM_ADDR"; break;
                case 1: regName = "SP_DRAM_ADDR"; break;
                case 2: regName = "SP_RD_LEN"; break;
                case 3: regName = "SP_WR_LEN"; break;
                case 4: regName = "SP_STATUS"; break;
                default: regName = "SP_CP0"; break;
            }

            string rspPcText = _hasActiveRspTracePc ? $"0x{_activeRspTracePc:x3}" : "<unavailable>";
            Common.Logger.PrintWarningLine(
                $"[N64RSPCP0] reg={reg & 0x1F}({regName}) value=0x{value:x8} rspPc={rspPcText} cpuPc=0x{Registers.R4300.PC:x8} " +
                $"spMem=0x{memAddr:x8} spDram=0x{dramAddr:x8} rdLen=0x{rdLen:x8} wrLen=0x{wrLen:x8} spStatus=0x{spStatus:x8} " +
                $"suspiciousLowDram={suspiciousLowDram}");
        }

        private void SetSiBusy(uint busyMask)
        {
            uint siStatus = ReadBigEndianWord(SI_STATUS_REG_R);
            siStatus &= ~(SiStatusDmaBusy | SiStatusIoBusy);
            siStatus |= busyMask & (SiStatusDmaBusy | SiStatusIoBusy);
            WriteBigEndianWord(SI_STATUS_REG_R, siStatus);
        }

        private void ProcessPifControlFlags()
        {
            byte flags = PIFRAM[63];
            byte clearMask = 0x00;

            if (flags == 0)
                return;

            // Minimal control handling for boot/runtime handshakes.
            if ((flags & 0x01) != 0)
                clearMask |= 0x01;

            if ((flags & 0x02) != 0)
                clearMask |= 0x02;

            if ((flags & 0x08) != 0)
                clearMask |= 0x08;

            if ((flags & 0x30) != 0)
            {
                PIFRAM[63] = 0x80;
                return;
            }

            PIFRAM[63] = (byte)(flags & ~clearMask);
        }

        private void ProcessPifJoybusCommands()
        {
            // Minimal Joybus handling for bring-up:
            // enough to satisfy common controller probe/read loops.
            byte pifControl = PIFRAM[63];
            int i = 0;
            while (i < 63)
            {
                byte tx = PIFRAM[i];
                if (tx == 0xFE)
                    break; // end marker

                if (tx == 0xFF || tx == 0xFD || tx == 0xB4)
                {
                    i++;
                    continue;
                }

                if (tx == 0x00)
                {
                    i++;
                    continue;
                }

                if (i + 2 >= 64)
                    break;

                int txLen = tx & 0x3F;
                int rxLen = PIFRAM[i + 1] & 0x3F;
                int cmdIndex = i + 2;
                int rxIndex = cmdIndex + txLen;

                if (txLen <= 0 || rxIndex >= 64)
                    break;

                byte cmd = PIFRAM[cmdIndex];
                switch (cmd)
                {
                    case 0x00: // INFO
                    case 0xFF: // RESET/INFO
                        // Standard controller signature.
                        if (rxLen >= 3 && rxIndex + 2 < 64)
                        {
                            PIFRAM[rxIndex + 0] = 0x05;
                            PIFRAM[rxIndex + 1] = 0x00;
                            PIFRAM[rxIndex + 2] = 0x01;
                        }
                        break;
                    case 0x01: // READ BUTTONS
                        if (rxLen >= 4 && rxIndex + 3 < 64)
                        {
                            PIFRAM[rxIndex + 0] = 0x00;
                            PIFRAM[rxIndex + 1] = 0x00;
                            PIFRAM[rxIndex + 2] = 0x00;
                            PIFRAM[rxIndex + 3] = 0x00;
                        }
                        break;
                    default:
                        // Unknown command: leave response bytes as-is/open.
                        break;
                }

                i = rxIndex + rxLen;
            }

            if (pifControl != 0)
                ProcessPifControlFlags();
        }

        struct MemEntry
        {
            public uint StartAddress;
            public uint EndAddress;
            public byte[] ReadArray;
            public byte[] WriteArray;
            public string Name;

            public MemoryEvent ReadEvent;
            public MemoryEvent WriteEvent;

            public MemEntry(uint StartAddress, uint EndAddress, byte[] ReadArray, byte[] WriteArray, string Name, MemoryEvent ReadEvent = null, MemoryEvent WriteEvent = null)
            {
                this.StartAddress = StartAddress;
                this.EndAddress   = EndAddress;
                this.ReadArray    = ReadArray;
                this.WriteArray   = WriteArray;
                this.Name         = Name;
                this.ReadEvent    = ReadEvent;
                this.WriteEvent   = WriteEvent;
            }
        }

        private MemEntry GetEntry(uint index)
        {
            if (TryGetSiAliasedEntry(index, out MemEntry siEntry))
                return siEntry;

            if (TryGetRcpRegisterAliasedEntry(index, out MemEntry rcpEntry))
                return rcpEntry;

            if (TryGetSpMirroredEntry(index, out MemEntry spEntry))
                return spEntry;

            // Robust fallback: treat the full RDRAM window as mapped even if table lookup
            // would miss for any reason. This prevents runaway OpenBus loops on normal RAM.
            if (index <= 0x03EFFFFF)
                return new MemEntry(0x00000000, 0x03EFFFFF, RDRAM, RDRAM, "RDRAM_FALLBACK");

            bool FoundEntry = false;
            MemEntry Result = new MemEntry();

            for (int i = 0; i < MemoryMap.Length; ++i)
            {
                MemEntry CurrentEntry = MemoryMap[i];
                if (index < CurrentEntry.StartAddress || index > CurrentEntry.EndAddress) continue;

                FoundEntry = true;
                Result = CurrentEntry;
                break;
            }

            if (!FoundEntry)
            {
                _openBusMissCount++;
                if (_openBusMissCount <= 64 || (_openBusMissCount % 256) == 0)
                {
                    Common.Logger.PrintWarningLine(
                        $"OpenBus read/write for unmapped address 0x{index:x8} at pc=0x{Registers.R4300.PC:x8} " +
                        $"(count={_openBusMissCount}).");
                }

                return new MemEntry(index & 0xFFFFFFFC, (index & 0xFFFFFFFC) + 3, OpenBus, OpenBus, "OPEN_BUS");
            }

            return Result;
        }

        private bool TryGetRcpRegisterAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();

            if (TryGetSpRegisterAliasedEntry(index, out entry))
                return true;

            if (TryGetDpcAliasedEntry(index, out entry))
                return true;

            if (TryGetDpsAliasedEntry(index, out entry))
                return true;

            if (TryGetMiAliasedEntry(index, out entry))
                return true;

            if (TryGetViAliasedEntry(index, out entry))
                return true;

            if (TryGetAiAliasedEntry(index, out entry))
                return true;

            if (TryGetPiAliasedEntry(index, out entry))
                return true;

            if (TryGetRiAliasedEntry(index, out entry))
                return true;

            return false;
        }

        private static bool IsMegaCallbackTraceAddress(uint address)
        {
            return address == 0x800D0F90u
                || address == 0x800D0FB8u
                || address == 0x800CFD88u
                || address == 0x800CFD90u
                || address == 0x800DFD88u
                || address == 0x800DFD90u
                || address == 0x80204984u;
        }

        private static bool IsMegaFatalTraceAddress(uint address)
        {
            return address == 0x801FFBB0u
                || address == 0x801FFBB4u
                || address == 0x80182BF0u
                || address == 0x80182BF4u
                || address == 0x80204830u
                || address == 0x80204978u
                || address == 0x800CFD88u
                || address == 0x800CFD90u
                || address == 0x801FFBB0u - 0x80000000u
                || address == 0x801FFBB4u - 0x80000000u
                || address == 0x80182BF0u - 0x80000000u
                || address == 0x80182BF4u - 0x80000000u
                || address == 0x80204830u - 0x80000000u
                || address == 0x80204978u - 0x80000000u
                || address == 0x800CFD88u - 0x80000000u
                || address == 0x800CFD90u - 0x80000000u;
        }

        private static bool IsMegaStatusTraceAddress(uint address)
        {
            return address == 0x80182BE8u
                || address == 0x80182BF0u
                || address == 0x80182BF4u
                || address == 0x800CFD88u
                || address == 0x800CFD90u
                || address == 0x801CC3C4u
                || address == 0x801CC3C8u
                || address == 0x801CC3C7u
                || address == 0x801CC3C9u
                || address == 0x00182BE8u
                || address == 0x00182BF0u
                || address == 0x00182BF4u
                || address == 0x000CFD88u
                || address == 0x000CFD90u
                || address == 0x001CC3C4u
                || address == 0x001CC3C8u
                || address == 0x001CC3C7u
                || address == 0x001CC3C9u;
        }

        private bool TryGetSpMirroredEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();

            // RSP DMEM/IMEM window is commonly mirrored through the 0x0400_xxxx range.
            // Mirror low 8KB so IPL accesses like 0xA40028xx don't fall to OpenBus.
            if (index < 0x04000000 || index > 0x0403FFFF)
                return false;

            uint mirrorOffset = (index - 0x04000000u) & 0x1FFFu;
            uint mirrorBase = index - mirrorOffset;

            if ((mirrorOffset & 0x1000u) == 0)
            {
                entry = new MemEntry(
                    mirrorBase,
                    mirrorBase + 0x0FFFu,
                    SP_DMEM_RW,
                    SP_DMEM_RW,
                    "SP_DMEM_MIRROR");
                return true;
            }

            entry = new MemEntry(
                mirrorBase,
                mirrorBase + 0x0FFFu,
                SP_IMEM_RW,
                SP_IMEM_RW,
                "SP_IMEM_MIRROR");
            return true;
        }

        private bool TryGetSpRegisterAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();

            if (index >= 0x04040000 && index <= 0x0407FFFF)
            {
                uint aliasBase = index & 0xFFFFFFE0u;
                uint regOffset = index & 0x1Fu;
                uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

                switch (regOffset & 0x1Cu)
                {
                    case 0x00:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_MEM_ADDR_REG_RW, SP_MEM_ADDR_REG_RW, "SP_MEM_ADDR_REG_MIRROR",
                            null, SP_MEM_ADDR_WRITE_EVENT);
                        return true;
                    case 0x04:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_DRAM_ADDR_REG_RW, SP_DRAM_ADDR_REG_RW, "SP_DRAM_ADDR_REG_MIRROR",
                            null, SP_DRAM_ADDR_WRITE_EVENT);
                        return true;
                    case 0x08:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_RD_LEN_REG_RW, SP_RD_LEN_REG_RW, "SP_RD_LEN_REG_MIRROR",
                            null, SP_RD_LEN_WRITE_EVENT);
                        return true;
                    case 0x0C:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_WR_LEN_REG_RW, SP_WR_LEN_REG_RW, "SP_WR_LEN_REG_MIRROR",
                            null, SP_WR_LEN_WRITE_EVENT);
                        return true;
                    case 0x10:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_STATUS_REG_R, SP_STATUS_REG_W, "SP_STATUS_REG_MIRROR",
                            null, SP_STATUS_WRITE_EVENT);
                        return true;
                    case 0x14:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_DMA_FULL_REG_R, SP_DMA_FULL_REG_W, "SP_DMA_FULL_REG_MIRROR");
                        return true;
                    case 0x18:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_DMA_BUSY_REG_R, SP_DMA_BUSY_REG_W, "SP_DMA_BUSY_REG_MIRROR");
                        return true;
                    case 0x1C:
                        entry = new MemEntry(wordBase, wordBase + 3, SP_SEMAPHORE_REG_R, SP_SEMAPHORE_REG_W, "SP_SEMAPHORE_REG_MIRROR",
                            SP_SEMAPHORE_READ_EVENT, null);
                        return true;
                }
            }

            if (index >= 0x04080000 && index <= 0x040FFFFF)
            {
                uint aliasBase = index & 0xFFFFFFE0u;
                uint regOffset = index & 0x1Fu;
                uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

                if ((regOffset & 0x1Cu) == 0x00)
                {
                    entry = new MemEntry(wordBase, wordBase + 3, SP_PC_REG_RW, SP_PC_REG_RW, "SP_PC_REG_MIRROR",
                        null, SP_PC_WRITE_EVENT);
                    return true;
                }

                if ((regOffset & 0x1Cu) == 0x04)
                {
                    entry = new MemEntry(wordBase, wordBase + 3, SP_IBIST_REG_RW, SP_IBIST_REG_RW, "SP_IBIST_REG_MIRROR");
                    return true;
                }
            }

            return false;
        }

        private bool TryGetDpcAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04100000 || index > 0x041FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFE0u;
            uint regOffset = index & 0x1Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x1Cu)
            {
                case 0x00:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_START_REG_RW, DPC_START_REG_RW, "DPC_START_REG_MIRROR", null, DPC_START_WRITE_EVENT);
                    return true;
                case 0x04:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_END_REG_RW, DPC_END_REG_RW, "DPC_END_REG_MIRROR", null, DPC_END_WRITE_EVENT);
                    return true;
                case 0x08:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_CURRENT_REG_RW, DPC_CURRENT_REG_RW, "DPC_CURRENT_REG_MIRROR");
                    return true;
                case 0x0C:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_STATUS_REG_R, DPC_STATUS_REG_W, "DPC_STATUS_REG_MIRROR", null, DPC_STATUS_WRITE_EVENT);
                    return true;
                case 0x10:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_CLOCK_REG_RW, DPC_CLOCK_REG_RW, "DPC_CLOCK_REG_MIRROR");
                    return true;
                case 0x14:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_BUFBUSY_REG_RW, DPC_BUFBUSY_REG_RW, "DPC_BUFBUSY_REG_MIRROR");
                    return true;
                case 0x18:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_PIPEBUSY_REG_RW, DPC_PIPEBUSY_REG_RW, "DPC_PIPEBUSY_REG_MIRROR");
                    return true;
                case 0x1C:
                    entry = new MemEntry(wordBase, wordBase + 3, DPC_TMEM_REG_RW, DPC_TMEM_REG_RW, "DPC_TMEM_REG_MIRROR");
                    return true;
            }

            return false;
        }

        private bool TryGetDpsAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04200000 || index > 0x042FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFE0u;
            uint regOffset = index & 0x1Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x0Cu)
            {
                case 0x00:
                    entry = new MemEntry(wordBase, wordBase + 3, DPS_TBIST_REG_RW, DPS_TBIST_REG_RW, "DPS_TBIST_REG_MIRROR");
                    return true;
                case 0x04:
                    entry = new MemEntry(wordBase, wordBase + 3, DPS_TEST_MODE_REG_RW, DPS_TEST_MODE_REG_RW, "DPS_TEST_MODE_REG_MIRROR");
                    return true;
                case 0x08:
                    entry = new MemEntry(wordBase, wordBase + 3, DPS_BUFTEST_ADDR_REG_RW, DPS_BUFTEST_ADDR_REG_RW, "DPS_BUFTEST_ADDR_REG_MIRROR");
                    return true;
                case 0x0C:
                    entry = new MemEntry(wordBase, wordBase + 3, DPS_BUFTEST_DATA_REG_RW, DPS_BUFTEST_DATA_REG_RW, "DPS_BUFTEST_DATA_REG_MIRROR");
                    return true;
            }

            return false;
        }

        private bool TryGetMiAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04300000 || index > 0x043FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFF0u;
            uint regOffset = index & 0x0Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x0Cu)
            {
                case 0x00:
                    entry = new MemEntry(wordBase, wordBase + 3, MI_INIT_MODE_REG_R, MI_INIT_MODE_REG_W, "MI_INIT_MODE_REG_MIRROR", null, MI_INIT_MODE_WRITE_EVENT);
                    return true;
                case 0x04:
                    entry = new MemEntry(wordBase, wordBase + 3, MI_VERSION_REG_RW, MI_VERSION_REG_RW, "MI_VERSION_REG_MIRROR");
                    return true;
                case 0x08:
                    entry = new MemEntry(wordBase, wordBase + 3, MI_INTR_REG_R, null, "MI_INTR_REG_MIRROR");
                    return true;
                case 0x0C:
                    entry = new MemEntry(wordBase, wordBase + 3, MI_INTR_MASK_REG_R, MI_INTR_MASK_REG_W, "MI_INTR_MASK_REG_MIRROR", null, MI_INTR_MASK_WRITE_EVENT);
                    return true;
            }

            return false;
        }

        private bool TryGetViAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04400000 || index > 0x044FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFC0u;
            uint regOffset = index & 0x3Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x3Cu)
            {
                case 0x00: entry = new MemEntry(wordBase, wordBase + 3, VI_STATUS_REG_RW, VI_STATUS_REG_RW, "VI_STATUS_REG_MIRROR", null, VI_STATUS_WRITE_EVENT); return true;
                case 0x04: entry = new MemEntry(wordBase, wordBase + 3, VI_ORIGIN_REG_RW, VI_ORIGIN_REG_RW, "VI_ORIGIN_REG_MIRROR", null, VI_ORIGIN_WRITE_EVENT); return true;
                case 0x08: entry = new MemEntry(wordBase, wordBase + 3, VI_WIDTH_REG_RW, VI_WIDTH_REG_RW, "VI_WIDTH_REG_MIRROR", null, VI_WIDTH_WRITE_EVENT); return true;
                case 0x0C: entry = new MemEntry(wordBase, wordBase + 3, VI_INTR_REG_RW, VI_INTR_REG_RW, "VI_INTR_REG_MIRROR", null, VI_INTR_WRITE_EVENT); return true;
                case 0x10: entry = new MemEntry(wordBase, wordBase + 3, VI_CURRENT_REG_RW, VI_CURRENT_REG_RW, "VI_CURRENT_REG_MIRROR", VI_CURRENT_READ_EVENT, VI_CURRENT_WRITE_EVENT); return true;
                case 0x14: entry = new MemEntry(wordBase, wordBase + 3, VI_BURST_REG_RW, VI_BURST_REG_RW, "VI_BURST_REG_MIRROR", null, VI_BURST_WRITE_EVENT); return true;
                case 0x18: entry = new MemEntry(wordBase, wordBase + 3, VI_V_SYNC_REG_RW, VI_V_SYNC_REG_RW, "VI_V_SYNC_REG_MIRROR", null, VI_V_SYNC_WRITE_EVENT); return true;
                case 0x1C: entry = new MemEntry(wordBase, wordBase + 3, VI_H_SYNC_REG_RW, VI_H_SYNC_REG_RW, "VI_H_SYNC_REG_MIRROR"); return true;
                case 0x20: entry = new MemEntry(wordBase, wordBase + 3, VI_LEAP_REG_RW, VI_LEAP_REG_RW, "VI_LEAP_REG_MIRROR"); return true;
                case 0x24: entry = new MemEntry(wordBase, wordBase + 3, VI_H_START_REG_RW, VI_H_START_REG_RW, "VI_H_START_REG_MIRROR", null, VI_H_START_WRITE_EVENT); return true;
                case 0x28: entry = new MemEntry(wordBase, wordBase + 3, VI_V_START_REG_RW, VI_V_START_REG_RW, "VI_V_START_REG_MIRROR", null, VI_V_START_WRITE_EVENT); return true;
                case 0x2C: entry = new MemEntry(wordBase, wordBase + 3, VI_V_BURST_REG_RW, VI_V_BURST_REG_RW, "VI_V_BURST_REG_MIRROR"); return true;
                case 0x30: entry = new MemEntry(wordBase, wordBase + 3, VI_X_SCALE_REG_RW, VI_X_SCALE_REG_RW, "VI_X_SCALE_REG_MIRROR", null, VI_X_SCALE_WRITE_EVENT); return true;
                case 0x34: entry = new MemEntry(wordBase, wordBase + 3, VI_Y_SCALE_REG_RW, VI_Y_SCALE_REG_RW, "VI_Y_SCALE_REG_MIRROR", null, VI_Y_SCALE_WRITE_EVENT); return true;
            }

            return false;
        }

        private bool TryGetAiAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04500000 || index > 0x045FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFE0u;
            uint regOffset = index & 0x1Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x1Cu)
            {
                case 0x00: entry = new MemEntry(wordBase, wordBase + 3, AI_DRAM_ADDR_REG_W, AI_DRAM_ADDR_REG_W, "AI_DRAM_ADDR_REG_MIRROR"); return true;
                case 0x04: entry = new MemEntry(wordBase, wordBase + 3, AI_LEN_REG_RW, AI_LEN_REG_RW, "AI_LEN_REG_MIRROR", AI_LEN_READ_EVENT, AI_LEN_WRITE_EVENT); return true;
                case 0x08: entry = new MemEntry(wordBase, wordBase + 3, AI_CONTROL_REG_W, AI_CONTROL_REG_W, "AI_CONTROL_REG_MIRROR"); return true;
                case 0x0C: entry = new MemEntry(wordBase, wordBase + 3, AI_STATUS_REG_R, AI_STATUS_REG_W, "AI_STATUS_REG_MIRROR", null, AI_STATUS_WRITE_EVENT); return true;
                case 0x10: entry = new MemEntry(wordBase, wordBase + 3, AI_DACRATE_REG_W, AI_DACRATE_REG_W, "AI_DACRATE_REG_MIRROR"); return true;
                case 0x14: entry = new MemEntry(wordBase, wordBase + 3, AI_BITRATE_REG_W, AI_BITRATE_REG_W, "AI_BITRATE_REG_MIRROR"); return true;
            }

            return false;
        }

        private bool TryGetPiAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04600000 || index > 0x046FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFC0u;
            uint regOffset = index & 0x3Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x3Cu)
            {
                case 0x00: entry = new MemEntry(wordBase, wordBase + 3, PI_DRAM_ADDR_REG_RW, PI_DRAM_ADDR_REG_RW, "PI_DRAM_ADDR_REG_MIRROR"); return true;
                case 0x04: entry = new MemEntry(wordBase, wordBase + 3, PI_CART_ADDR_REG_RW, PI_CART_ADDR_REG_RW, "PI_CART_ADDR_REG_MIRROR"); return true;
                case 0x08: entry = new MemEntry(wordBase, wordBase + 3, PI_RD_LEN_REG_RW, PI_RD_LEN_REG_RW, "PI_RD_LEN_REG_MIRROR", null, PI_RD_LEN_WRITE_EVENT); return true;
                case 0x0C: entry = new MemEntry(wordBase, wordBase + 3, PI_WR_LEN_REG_RW, PI_WR_LEN_REG_RW, "PI_WR_LEN_REG_MIRROR", null, PI_WR_LEN_WRITE_EVENT); return true;
                case 0x10: entry = new MemEntry(wordBase, wordBase + 3, PI_STATUS_REG_R, PI_STATUS_REG_W, "PI_STATUS_REG_MIRROR", PI_STATUS_READ_EVENT, PI_STATUS_WRITE_EVENT); return true;
                case 0x14: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM1_LAT_REG_RW, PI_BSD_DOM1_LAT_REG_RW, "PI_BSD_DOM1_LAT_REG_MIRROR"); return true;
                case 0x18: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM1_PWD_REG_RW, PI_BSD_DOM1_PWD_REG_RW, "PI_BSD_DOM1_PWD_REG_MIRROR"); return true;
                case 0x1C: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM1_PGS_REG_RW, PI_BSD_DOM1_PGS_REG_RW, "PI_BSD_DOM1_PGS_REG_MIRROR"); return true;
                case 0x20: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM1_RLS_REG_RW, PI_BSD_DOM1_RLS_REG_RW, "PI_BSD_DOM1_RLS_REG_MIRROR"); return true;
                case 0x24: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM2_LAT_REG_RW, PI_BSD_DOM2_LAT_REG_RW, "PI_BSD_DOM2_LAT_REG_MIRROR"); return true;
                case 0x28: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM2_PWD_REG_RW, PI_BSD_DOM2_PWD_REG_RW, "PI_BSD_DOM2_PWD_REG_MIRROR"); return true;
                case 0x2C: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM2_PGS_REG_RW, PI_BSD_DOM2_PGS_REG_RW, "PI_BSD_DOM2_PGS_REG_MIRROR"); return true;
                case 0x30: entry = new MemEntry(wordBase, wordBase + 3, PI_BSD_DOM2_RLS_REG_RW, PI_BSD_DOM2_RLS_REG_RW, "PI_BSD_DOM2_RLS_REG_MIRROR"); return true;
            }

            return false;
        }

        private bool TryGetRiAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();
            if (index < 0x04700000 || index > 0x047FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFE0u;
            uint regOffset = index & 0x1Fu;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFCu);

            switch (regOffset & 0x1Cu)
            {
                case 0x00: entry = new MemEntry(wordBase, wordBase + 3, RI_MODE_REG_RW, RI_MODE_REG_RW, "RI_MODE_REG_MIRROR"); return true;
                case 0x04: entry = new MemEntry(wordBase, wordBase + 3, RI_CONFIG_REG_RW, RI_CONFIG_REG_RW, "RI_CONFIG_REG_MIRROR"); return true;
                case 0x08: entry = new MemEntry(wordBase, wordBase + 3, RI_CURRENT_LOAD_REG_RW, RI_CURRENT_LOAD_REG_RW, "RI_CURRENT_LOAD_REG_MIRROR"); return true;
                case 0x0C: entry = new MemEntry(wordBase, wordBase + 3, RI_SELECT_REG_RW, RI_SELECT_REG_RW, "RI_SELECT_REG_MIRROR"); return true;
                case 0x10: entry = new MemEntry(wordBase, wordBase + 3, RI_REFRESH_REG_RW, RI_REFRESH_REG_RW, "RI_REFRESH_REG_MIRROR"); return true;
                case 0x14: entry = new MemEntry(wordBase, wordBase + 3, RI_LATENCY_REG_RW, RI_LATENCY_REG_RW, "RI_LATENCY_REG_MIRROR"); return true;
                case 0x18: entry = new MemEntry(wordBase, wordBase + 3, RI_ERROR_REG_RW, RI_ERROR_REG_RW, "RI_ERROR_REG_MIRROR"); return true;
                case 0x1C: entry = new MemEntry(wordBase, wordBase + 3, RI_WERROR_REG_RW, RI_WERROR_REG_RW, "RI_WERROR_REG_MIRROR"); return true;
            }

            return false;
        }

        private bool TryGetSiAliasedEntry(uint index, out MemEntry entry)
        {
            entry = new MemEntry();

            if (index < 0x04800000 || index > 0x048FFFFF)
                return false;

            uint aliasBase = index & 0xFFFFFFE0;
            uint regOffset = index & 0x1F;
            uint regAddr = 0x04800000 + regOffset;
            uint wordBase = aliasBase + (regOffset & 0xFFFFFFFC);

            if (regAddr >= 0x04800000 && regAddr <= 0x04800003)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_DRAM_ADDR_REG_RW, SI_DRAM_ADDR_REG_RW, "SI_DRAM_ADDR_REG_MIRROR");
                return true;
            }

            if (regAddr >= 0x04800004 && regAddr <= 0x04800007)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_PIF_ADDR_RD64B_REG_RW, SI_PIF_ADDR_RD64B_REG_RW, "SI_PIF_ADDR_RD64B_REG_MIRROR",
                    null, SI_PIF_ADDR_RD64B_WRITE_EVENT);
                return true;
            }

            if (regAddr >= 0x04800010 && regAddr <= 0x04800013)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_PIF_ADDR_WR64B_REG_RW, SI_PIF_ADDR_WR64B_REG_RW, "SI_PIF_ADDR_WR64B_REG_MIRROR",
                    null, SI_PIF_ADDR_WR64B_WRITE_EVENT);
                return true;
            }

            if (regAddr >= 0x04800018 && regAddr <= 0x0480001B)
            {
                entry = new MemEntry(wordBase, wordBase + 3, SI_STATUS_REG_R, SI_STATUS_REG_W, "SI_STATUS_REG_MIRROR",
                    null, SI_STATUS_WRITE_EVENT);
                return true;
            }

            // Bring-up fallback: allow unknown SI alias offsets to behave as RAM-like mirrors
            // instead of OpenBus to avoid lockups in early exception/controller loops.
            if (index <= 0x048FFFFF)
            {
                entry = new MemEntry(0x04800000, 0x048FFFFF, SI_MIRROR_RAM, SI_MIRROR_RAM, "SI_MIRROR_RAM");
                return true;
            }

            return false;
        }

        public byte this[uint index]
        {
            get
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: false);
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.ReadArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not readable.");

                if (Entry.ReadEvent != null
                    && (Entry.ReadEvent == AI_LEN_READ_EVENT
                        || Entry.ReadEvent == PI_STATUS_READ_EVENT
                        || Entry.ReadEvent == VI_CURRENT_READ_EVENT))
                {
                    Entry.ReadEvent();
                }

                int offset = ResolveArrayOffset(Entry.ReadArray, nonCachedIndex - Entry.StartAddress);
                byte value = Entry.ReadArray[offset];
                uint regOffset = nonCachedIndex - Entry.StartAddress;
                if ((regOffset & 0x3) == 0x3
                    && Entry.ReadEvent != null
                    && Entry.ReadEvent != AI_LEN_READ_EVENT
                    && Entry.ReadEvent != PI_STATUS_READ_EVENT
                    && Entry.ReadEvent != VI_CURRENT_READ_EVENT)
                    Entry.ReadEvent?.Invoke();
                return value;
            }
            set
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: true);
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.WriteArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not writable.");

                int offset = ResolveArrayOffset(Entry.WriteArray, nonCachedIndex - Entry.StartAddress);
                Entry.WriteArray[offset] = value;
                uint regOffset = nonCachedIndex - Entry.StartAddress;
                if ((regOffset & 0x3) == 0x3)
                    Entry.WriteEvent?.Invoke();
            }
        }

        public byte[] this[uint index, int size]
        {
            get
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: false);
                byte[] result = new byte[size];
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.ReadArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not readable.");

                // Read byte-by-byte to safely handle accesses that span memory map boundaries.
                for (int i = 0; i < size; i++)
                    result[i] = this[index + (uint)i];

                return result;
            }
            set
            {
                uint nonCachedIndex = ToPhysicalAddress(index, isWrite: true);
                MemEntry Entry = GetEntry(nonCachedIndex);

                if (Entry.WriteArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"Memory at \"0x{index:x8}\" is not writable.");

                // Write byte-by-byte to safely handle accesses that span memory map boundaries.
                for (int i = 0; i < size; i++)
                    this[index + (uint)i] = value[i];
            }
        }

        public void FastMemoryWrite(uint Dest, byte[] ToWrite)
        {
            TryTraceWatchRangeBulkWrite("fast-write", Dest, source: null, ToWrite.Length);
            this[Dest, ToWrite.Length] = ToWrite;
        }

        public byte[] FastMemoryRead(uint Source, int Length)
        {
            return this[Source, Length];
        }

        public void FastMemoryWrite(uint Dest, byte[] ToWrite, int Length)
        {
            if (ToWrite.Length < Length)
                throw new InvalidOperationException("Cannot write to memory an Array that is less than the input size.");
            TryTraceWatchRangeBulkWrite("fast-write", Dest, source: null, Length);
            this[Dest, Length] = ToWrite;
        }

        public void FastMemoryCopy(uint Dest, uint Source, int Size)
        {
            TryTraceWatchRangeBulkWrite("fast-copy", Dest, Source, Size);
            FastMemoryWrite(Dest, FastMemoryRead(Source, Size));
        }

        public void SafeMemoryCopy(uint Dest, uint Source, int Size)
        {
            if (GetEntry(Source & 0x1FFFFFFF).StartAddress != GetEntry((Source + (uint)Size) & 0x1FFFFFFF).StartAddress 
                || GetEntry(Dest & 0x1FFFFFFF).StartAddress != GetEntry((Dest + (uint)Size) & 0x1FFFFFFF).StartAddress)
                throw new NotImplementedException("Copying over multiple Memory Regions isn't implemented.");
            FastMemoryCopy(Dest, Source, Size);
        }

        public byte ReadUInt8(uint index)
        {
            return this[index];
        }

        private void TryTraceWatchRangeBulkWrite(string op, uint dest, uint? source, int size)
        {
            if (size <= 0)
                return;

            uint physical = 0;
            bool havePhysical = false;
            try
            {
                physical = ToPhysicalAddress(dest, isWrite: true);
                havePhysical = true;
            }
            catch
            {
                // Best effort trace only.
            }

            if (!havePhysical || !ShouldTraceWatchRange(dest, physical, (uint)size))
                return;

            if (_traceWatchRangeLogCount >= TraceWatchRangeLogLimit)
                return;

            _traceWatchRangeLogCount++;
            Common.Logger.PrintWarningLine(
                $"[N64WATCH] {op} dest=0x{dest:x8} phys=0x{physical:x8} " +
                (source.HasValue ? $"src=0x{source.Value:x8} " : string.Empty) +
                $"size=0x{size:x} pc=0x{Registers.R4300.PC:x8}");
        }

        public void WriteUInt8(uint index, byte value)
        {
            uint physical = 0;
            bool havePhysical = false;
            byte oldValue = 0;
            try
            {
                physical = ToPhysicalAddress(index, isWrite: true);
                havePhysical = true;
                if ((TraceExceptionVectorWrites && IsExceptionVectorPhysicalAddress(physical, 1))
                    || (TraceLowRamMutationWrites && IsLowRamDiagnosticPhysicalAddress(physical, 1)))
                    oldValue = ReadUInt8(index);
            }
            catch
            {
                // Best effort watch logging.
            }

            if (TraceWatchAddress.HasValue)
            {
                uint watched = TraceWatchAddress.Value;

                if (index == watched || (havePhysical && physical == watched))
                {
                    byte watchedOldValue = oldValue;
                    if (!havePhysical || !IsExceptionVectorPhysicalAddress(physical, 1))
                    {
                        try { watchedOldValue = ReadUInt8(index); } catch { }
                    }
                    Common.Logger.PrintWarningLine(
                        $"[N64WATCH] write8 addr=0x{index:x8}" +
                        (havePhysical ? $" phys=0x{physical:x8}" : string.Empty) +
                        $" old=0x{watchedOldValue:x2} new=0x{value:x2} pc=0x{Registers.R4300.PC:x8}");
                }
            }

            if (havePhysical
                && physical <= 0x00000300u
                && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64LOWRAMCPU8] old=0x{oldValue:x2} new=0x{value:x2} {BuildLowRamStoreContext(index, physical, 1)}");
            }

            if (havePhysical && ShouldTraceWatchRange(index, physical, 1))
                TraceWatchRangeWrite("write8", index, physical, value);

            if (havePhysical && ShouldTraceSpRegisterStore(physical))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPMMIO] write8 reg={DescribeSpRegisterStore(physical)} addr=0x{index:x8} phys=0x{physical:x8} " +
                    $"old=0x{oldValue:x2} new=0x{value:x2} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }

            this[index] = value;
            if (havePhysical)
            {
                if (physical < RDRAM.Length)
                    NoteRdramWriteRange(physical, 1);
                TraceLowRamMutationWrite("write8", index, physical, oldValue, value, 1);
                TraceExceptionVectorWrite("write8", index, physical, oldValue, value, 1);
            }
        }

        public sbyte ReadInt8(uint index)
        {
            return (sbyte)ReadUInt8(index);
        }

        public void WriteInt8(uint index, sbyte value)
        {
            WriteUInt8(index, (byte)value);
        }

        public ushort ReadUInt16(uint index)
        {
            byte[] Res = this[index, 2];
            Array.Reverse(Res);
            unsafe
            {
                fixed (byte* point = &Res[0])
                {
                    ushort* shortPoint = (ushort*)point;
                    return *shortPoint;
                }
            }
        }

        public void WriteUInt16(uint index, ushort value)
        {
            uint physical = 0;
            bool havePhysical = false;
            ushort oldValue = 0;
            try
            {
                physical = ToPhysicalAddress(index, isWrite: true);
                havePhysical = true;
                if ((TraceExceptionVectorWrites && IsExceptionVectorPhysicalAddress(physical, 2))
                    || (TraceLowRamMutationWrites && IsLowRamDiagnosticPhysicalAddress(physical, 2)))
                    oldValue = ReadUInt16(index);
            }
            catch
            {
                // Best effort watch logging.
            }

            if (TraceWatchAddress.HasValue)
            {
                uint watched = TraceWatchAddress.Value;

                if (index == watched || (havePhysical && physical == watched))
                {
                    ushort watchedOldValue = oldValue;
                    if (!havePhysical || !IsExceptionVectorPhysicalAddress(physical, 2))
                    {
                        try { watchedOldValue = ReadUInt16(index); } catch { }
                    }
                    Common.Logger.PrintWarningLine(
                        $"[N64WATCH] write16 addr=0x{index:x8}" +
                        (havePhysical ? $" phys=0x{physical:x8}" : string.Empty) +
                        $" old=0x{watchedOldValue:x4} new=0x{value:x4} pc=0x{Registers.R4300.PC:x8}");
                }
            }

            if (havePhysical
                && physical <= 0x00000300u
                && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64LOWRAMCPU16] old=0x{oldValue:x4} new=0x{value:x4} {BuildLowRamStoreContext(index, physical, 2)}");
            }

            if (havePhysical && ShouldTraceWatchRange(index, physical, 2))
                TraceWatchRangeWrite("write16", index, physical, value);

            if (havePhysical && ShouldTraceSpRegisterStore(physical))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPMMIO] write16 reg={DescribeSpRegisterStore(physical)} addr=0x{index:x8} phys=0x{physical:x8} " +
                    $"old=0x{oldValue:x4} new=0x{value:x4} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }

            unsafe
            {
                ushort* point = &value;
                byte[] PointArray = new byte[2];
                Marshal.Copy(new IntPtr(point), PointArray, 0, 2);

                Array.Reverse(PointArray);

                this[index, 2] = PointArray;
            }

            if (havePhysical)
            {
                if (physical < RDRAM.Length)
                    NoteRdramWriteRange(physical, 2);
                TraceLowRamMutationWrite("write16", index, physical, oldValue, value, 2);
                TraceExceptionVectorWrite("write16", index, physical, oldValue, value, 2);
            }
        }

        public short ReadInt16(uint index)
        {
            return (short)ReadUInt16(index);
        }

        public void WriteInt16(uint index, short value)
        {
            WriteUInt16(index, (ushort)value);
        }

        public uint ReadUInt32(uint index)
        {
            uint physical = 0;
            bool havePhysical = false;
            try
            {
                physical = ToPhysicalAddress(index, isWrite: false);
                havePhysical = true;
            }
            catch
            {
                // Preserve existing exception behavior below.
            }

            byte[] Res = this[index, 4];
            Array.Reverse(Res);
            unsafe
            {
                fixed (byte* point = &Res[0])
                {
                    uint* intPoint = (uint*)point;
                    uint value = *intPoint;
                    if (havePhysical && ShouldTraceWatchRange(index, physical))
                        TraceWatchRangeAccess("read32", index, physical, value);
                    return value;
                }
            }
        }

        public void WriteUInt32(uint index, uint value)
        {
            uint physical = 0;
            bool havePhysical = false;
            uint oldValue = 0;
            try
            {
                physical = ToPhysicalAddress(index, isWrite: true);
                havePhysical = true;
                if ((TraceExceptionVectorWrites && IsExceptionVectorPhysicalAddress(physical, 4))
                    || (TraceLowRamMutationWrites && IsLowRamDiagnosticPhysicalAddress(physical, 4)))
                    oldValue = ReadUInt32(index);
            }
            catch
            {
                // Preserve existing exception behavior below.
            }

            if (TraceWatchAddress.HasValue)
            {
                uint watched = TraceWatchAddress.Value;
                try
                {
                    if (!havePhysical)
                    {
                        physical = ToPhysicalAddress(index, isWrite: true);
                        havePhysical = true;
                    }
                }
                catch
                {
                    // Best effort watch logging.
                }

                if (index == watched || (havePhysical && physical == watched))
                {
                    uint watchedOldValue = oldValue;
                    if (!havePhysical || !IsExceptionVectorPhysicalAddress(physical, 4))
                    {
                        try { watchedOldValue = ReadUInt32(index); } catch { }
                    }
                    Common.Logger.PrintWarningLine(
                        $"[N64WATCH] write32 addr=0x{index:x8}" +
                        (havePhysical ? $" phys=0x{physical:x8}" : string.Empty) +
                        $" old=0x{watchedOldValue:x8} new=0x{value:x8} pc=0x{Registers.R4300.PC:x8}");
                }
            }

            if (havePhysical && ShouldTraceWatchRange(index, physical))
                TraceWatchRangeWrite("write32", index, physical, value);

            if (havePhysical
                && physical <= 0x00000300u
                && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64LOWRAMCPU] write32 pc=0x{Registers.R4300.PC:x8} vaddr=0x{index:x8} phys=0x{physical:x8} " +
                    $"old=0x{oldValue:x8} new=0x{value:x8} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            if (havePhysical && ShouldTraceSpRegisterStore(physical))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPMMIO] write32 reg={DescribeSpRegisterStore(physical)} addr=0x{index:x8} phys=0x{physical:x8} " +
                    $"old=0x{oldValue:x8} new=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }

            if (TraceMegaCallbackBlock)
            {
                bool traceVirtual = IsMegaCallbackTraceAddress(index);
                bool tracePhysical = havePhysical && IsMegaCallbackTraceAddress(physical);
                if (traceVirtual || tracePhysical)
                {
                    uint traceOldValue = 0;
                    try { traceOldValue = ReadUInt32(index); } catch { }
                    Common.Logger.PrintWarningLine(
                        $"[N64MEGACB] write32 addr=0x{index:x8}" +
                        (havePhysical ? $" phys=0x{physical:x8}" : string.Empty) +
                        $" old=0x{traceOldValue:x8} new=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
                }
            }

            if (TraceMegaFatalBlock)
            {
                bool traceVirtual = IsMegaFatalTraceAddress(index);
                bool tracePhysical = havePhysical && IsMegaFatalTraceAddress(physical);
                if (traceVirtual || tracePhysical)
                {
                    uint traceOldValue = 0;
                    try { traceOldValue = ReadUInt32(index); } catch { }
                    Common.Logger.PrintWarningLine(
                        $"[N64MEGAFB] write32 addr=0x{index:x8}" +
                        (havePhysical ? $" phys=0x{physical:x8}" : string.Empty) +
                        $" old=0x{traceOldValue:x8} new=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
                }
            }

            if (TraceMegaStatusBlock)
            {
                bool traceVirtual = IsMegaStatusTraceAddress(index);
                bool tracePhysical = havePhysical && IsMegaStatusTraceAddress(physical);
                if (traceVirtual || tracePhysical)
                {
                    uint traceOldValue = 0;
                    try { traceOldValue = ReadUInt32(index); } catch { }
                    Common.Logger.PrintWarningLine(
                        $"[N64MEGASTATUSW] write32 addr=0x{index:x8}" +
                        (havePhysical ? $" phys=0x{physical:x8}" : string.Empty) +
                        $" old=0x{traceOldValue:x8} new=0x{value:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
                }
            }

            if (TraceSm64SlotWrites)
            {
                uint nonCachedIndex = havePhysical ? physical : ToPhysicalAddress(index, isWrite: true);
                if (nonCachedIndex == 0x003359A8u)
                {
                    uint traceOldValue = 0;
                    try { traceOldValue = ReadUInt32(index); } catch { }
                    Console.WriteLine(
                        $"[N64SM64SLOT] write [0x{index:x8}/phys 0x{nonCachedIndex:x8}] old=0x{traceOldValue:x8} new=0x{value:x8} pc=0x{Registers.R4300.PC:x8}");
                }
            }

            unsafe
            {
                uint* point = &value;
                byte[] PointArray = new byte[4];
                Marshal.Copy(new IntPtr(point), PointArray, 0, 4);

                Array.Reverse(PointArray);

                this[index, 4] = PointArray;
            }

            if (havePhysical)
            {
                if (physical < RDRAM.Length)
                    NoteRdramWriteRange(physical, 4);
                TraceLowRamMutationWrite("write32", index, physical, oldValue, value, 4);
                TraceExceptionVectorWrite("write32", index, physical, oldValue, value, 4);
            }
        }

        public int ReadInt32(uint index)
        {
            return (int)ReadUInt32(index);
        }

        public void WriteInt32(uint index, int value)
        {
            WriteUInt32(index, (uint)value);
        }

        public ulong ReadUInt64(uint index)
        {
            byte[] Res = this[index, 8];
            Array.Reverse(Res);
            unsafe
            {
                fixed (byte* point = &Res[0])
                {
                    ulong* longPoint = (ulong*)point;
                    return *longPoint;
                }
            }
        }

        public void WriteUInt64(uint index, ulong value)
        {
            unsafe
            {
                ulong* point = &value;
                byte[] PointArray = new byte[8];
                Marshal.Copy(new IntPtr(point), PointArray, 0, 8);

                Array.Reverse(PointArray);

                this[index, 8] = PointArray;
            }
        }

        public long ReadInt64(uint index)
        {
            return (long)ReadUInt64(index);
        }

        public void WriteInt64(uint index, long value)
        {
            WriteUInt64(index, (ulong)value);
        }

        private static int ResolveArrayOffset(byte[] array, uint logicalOffset)
        {
            if (array.Length == 0)
                throw new IndexOutOfRangeException("Mapped memory region has zero length.");

            if (logicalOffset < (uint)array.Length)
                return (int)logicalOffset;

            // Cartridge and some register regions are mirrored over larger address windows.
            return (int)(logicalOffset % (uint)array.Length);
        }

        private bool TryGetCartridgeRomDmaSource(uint sourcePhysical, MemEntry srcEntry, out int romOffset, out int romContig)
        {
            romOffset = 0;
            romContig = 0;

            if (!ReferenceEquals(srcEntry.ReadArray, _rom))
                return false;

            uint windowOffset = sourcePhysical - srcEntry.StartAddress;
            if (windowOffset >= (uint)_rom.Length)
            {
                romOffset = _rom.Length;
                romContig = 0;
                return true;
            }

            romOffset = (int)windowOffset;
            romContig = _rom.Length - romOffset;
            return true;
        }

        private static uint ToPhysicalAddress(uint virtualAddress, bool isWrite = false)
        {
            // VR4300 virtual address segments:
            // kseg0: 0x8000_0000..0x9FFF_FFFF (direct-mapped, cached)
            // kseg1: 0xA000_0000..0xBFFF_FFFF (direct-mapped, uncached)
            // Others use TLB translation.
            //
            // Bring-up compatibility:
            // Some early boot code accesses low physical-looking addresses before TLB state
            // is fully established. Keep this pass-through strictly to early IPL windows,
            // otherwise user-space/kuseg code can bypass TLB and execute garbage.
            if (AllowDirectLowPhysicalWindow && virtualAddress < 0x20000000u)
            {
                uint pc = Registers.R4300.PC;
                bool earlyBootPc =
                    (pc >= 0xA4000000u && pc <= 0xA4001FFFu) ||
                    (pc >= 0x80000000u && pc <= 0x80001FFFu) ||
                    (pc >= 0xBFC00000u && pc <= 0xBFC00FFFu);
                if (earlyBootPc && virtualAddress < 0x05000000u)
                    return virtualAddress;
            }

            uint segment = virtualAddress & 0xE0000000u;
            if (segment == 0x80000000u || segment == 0xA0000000u)
                return virtualAddress & 0x1FFFFFFFu;

            try
            {
                return TLB.TranslateAddress(virtualAddress, throwOnMiss: StrictDataTlb, isStore: isWrite) & 0x1FFFFFFFu;
            }
            catch (Common.Exceptions.TLBMissException)
            {
                // Bring-up compromise:
                // keep instruction-side TLB strict, but allow low data addresses to
                // fall back to direct physical mapping on miss so software can leave
                // early refill loops before full TLB behavior is implemented.
                //
                // Never apply this fallback for the first page (null/near-null pointers),
                // otherwise invalid pointer walks can silently read RDRAM at 0x00000000
                // and derail exception-list logic (seen in SM64 startup).
                if (AllowDirectLowPhysicalWindow
                    && AllowLowPhysicalFallbackOnTlbMiss
                    && (virtualAddress >= 0x00001000u || AllowNullPageFallbackOnTlbMiss)
                    && virtualAddress < 0x05000000u)
                {
                    return virtualAddress;
                }

                throw;
            }
        }

        private static uint PhysicalToKseg1(uint physicalAddress)
        {
            return 0xA0000000u | (physicalAddress & 0x1FFFFFFFu);
        }

        private uint ReadUInt32Physical(uint physicalAddress)
        {
            return ReadUInt32(PhysicalToKseg1(physicalAddress));
        }

        public byte ReadUInt8PhysicalUncached(uint physicalAddress)
        {
            return ReadUInt8(PhysicalToKseg1(physicalAddress));
        }

        private void WriteUInt32Physical(uint physicalAddress, uint value)
        {
            WriteUInt32(PhysicalToKseg1(physicalAddress), value);
        }

        private void DmaCopyPhysical(uint destPhysical, uint sourcePhysical, int size)
        {
            uint dest = destPhysical & 0x1FFFFFFFu;
            uint src = sourcePhysical & 0x1FFFFFFFu;
            int remaining = size;

            while (remaining > 0)
            {
                MemEntry srcEntry = GetEntry(src);
                MemEntry dstEntry = GetEntry(dest);

                if (srcEntry.ReadArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"DMA source 0x{src:x8} not readable.");
                if (dstEntry.WriteArray == null)
                    throw new Common.Exceptions.MemoryProtectionViolation($"DMA destination 0x{dest:x8} not writable.");

                int dstOff = ResolveArrayOffset(dstEntry.WriteArray, dest - dstEntry.StartAddress);
                int dstContig = dstEntry.WriteArray.Length - dstOff;
                int chunk = Math.Min(remaining, dstContig);
                int srcOff;
                int copyCount;
                bool zeroFillFromRom = false;
                byte[] srcArray = srcEntry.ReadArray;

                if (TryGetCartridgeRomDmaSource(src, srcEntry, out int romOffset, out int romContig))
                {
                    srcOff = romOffset;
                    copyCount = Math.Min(chunk, romContig);
                    zeroFillFromRom = copyCount < chunk;
                }
                else
                {
                    srcOff = ResolveArrayOffset(srcEntry.ReadArray, src - srcEntry.StartAddress);
                    int srcContig = srcEntry.ReadArray.Length - srcOff;
                    chunk = Math.Min(chunk, srcContig);
                    copyCount = chunk;
                }

                Dictionary<uint, uint> lowRamOldWords = null;

                if (TraceLowRamMutationWrites && RangeOverlaps(dest, (uint)chunk, 0x00000100u, 0x000003FFu))
                {
                    lowRamOldWords = new Dictionary<uint, uint>();
                    uint wordStart = Math.Max(dest, 0x00000100u) & ~0x3u;
                    uint wordEnd = Math.Min(dest + (uint)Math.Max(0, chunk - 1), 0x000003FFu) & ~0x3u;
                    for (uint word = wordStart; word <= wordEnd; word += 4)
                        lowRamOldWords[word] = ReadUInt32Physical(word);
                }

                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
                {
                    uint destEnd = dest + (uint)Math.Max(0, chunk - 1);
                    if (dest <= 0x00000300u && destEnd >= 0x00000000u)
                    {
                        Common.Logger.PrintWarningLine(
                            $"[N64LOWRAMDMA] pc=0x{Registers.R4300.PC:x8} " +
                            $"dest=0x{dest:x8} src=0x{src:x8} chunk=0x{chunk:x} remaining=0x{remaining:x} " +
                            $"piDram=0x{ReadBigEndianWord(PI_DRAM_ADDR_REG_RW):x8} piCart=0x{ReadBigEndianWord(PI_CART_ADDR_REG_RW):x8}");
                    }
                }

                if (copyCount > 0)
                    Buffer.BlockCopy(srcArray, srcOff, dstEntry.WriteArray, dstOff, copyCount);

                if (zeroFillFromRom)
                {
                    Array.Clear(dstEntry.WriteArray, dstOff + copyCount, chunk - copyCount);

                    if (TracePiInterruptLifecycle
                        || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
                    {
                        Common.Logger.PrintWarningLine(
                            $"[N64PIDMA] rom-zero-fill pc=0x{Registers.R4300.PC:x8} " +
                            $"src=0x{src:x8} dest=0x{dest:x8} chunk=0x{chunk:x} copied=0x{copyCount:x} " +
                            $"cartWindow={srcEntry.Name} romSize=0x{_rom.Length:x}");
                    }
                }

                if (dest < RDRAM.Length)
                    NoteRdramWriteRange(dest, (uint)chunk);

                if (lowRamOldWords != null)
                {
                    foreach (KeyValuePair<uint, uint> entry in lowRamOldWords)
                    {
                        uint newValue = ReadUInt32Physical(entry.Key);
                        TraceLowRamMutationWrite("dma32", PhysicalToKseg1(entry.Key), entry.Key, entry.Value, newValue, 4);
                    }
                }

                remaining -= chunk;
                src += (uint)chunk;
                dest += (uint)chunk;
            }
        }

        private void InvokeMappedReadEvent(uint index)
        {
            uint physical = ToPhysicalAddress(index, isWrite: false);
            MemEntry entry = GetEntry(physical);
            entry.ReadEvent?.Invoke();
        }

        private void InvokeMappedWriteEvent(uint index)
        {
            uint physical = ToPhysicalAddress(index, isWrite: true);
            MemEntry entry = GetEntry(physical);
            entry.WriteEvent?.Invoke();
        }
    }
}
