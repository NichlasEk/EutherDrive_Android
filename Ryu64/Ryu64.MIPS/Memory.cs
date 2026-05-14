using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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
        private static readonly bool TraceFramebufferLifecycle =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_FB"), "1", StringComparison.Ordinal);
        private static readonly bool TraceRdpCommands =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RDP_COMMANDS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceRdpTextureRectangles =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_RDP_TEXRECT"), "1", StringComparison.Ordinal);
        private static readonly bool EnableRdpDepth =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RDP_DEPTH"), "0", StringComparison.Ordinal);
        private static readonly bool UseReferenceTexRectFlip =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_REFERENCE_TEXRECT_FLIP"), "0", StringComparison.Ordinal);
        private static readonly bool UseReferenceTexRectExtents =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_REFERENCE_TEXRECT_EXTENTS"), "1", StringComparison.Ordinal);
        private static readonly bool EnableRdpPerspectiveTexture =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RDP_PERSPECTIVE_TEXTURE"), "0", StringComparison.Ordinal);
        private static readonly bool EnableTexRectSolidFallback =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_TEXRECT_SOLID_FALLBACK"), "1", StringComparison.Ordinal);
        private static readonly bool EnableN64Perf =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_PERF"), "1", StringComparison.Ordinal);
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
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_EXCEPTION_VECTOR_WRITES"), "1", StringComparison.Ordinal);
        private static readonly bool TraceLowRamMutationWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_LOW_RAM_MUTATIONS"), "1", StringComparison.Ordinal);
        private static readonly bool MirrorPiRdLenAsCartToDram =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_PI_RDLEN_MIRROR"), "1", StringComparison.Ordinal);
        private static readonly bool FastPiStatusPoll =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_FAST_PI_POLL"), "0", StringComparison.Ordinal);
        private static readonly bool SuppressDpInterrupt =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_SUPPRESS_DP_IRQ"), "1", StringComparison.Ordinal);
        private static readonly bool MupenStyleDpcStatus =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_MUPEN_DPC_STATUS"), "0", StringComparison.Ordinal);
        private static readonly bool TraceSm64SlotWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SM64_SLOT_WRITES"), "1", StringComparison.Ordinal);
        private static readonly ushort N64ControllerButtons = ParseN64ControllerButtons();
        private static readonly sbyte N64ControllerAnalogX = ParseSByteEnvironment("EUTHERDRIVE_N64_ANALOG_X");
        private static readonly sbyte N64ControllerAnalogY = ParseSByteEnvironment("EUTHERDRIVE_N64_ANALOG_Y");
        private static readonly bool AutoCompleteRspTaskOnHaltClear =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_SP_AUTOCOMPLETE"), "1", StringComparison.Ordinal);
        private static readonly bool EnableRspTaskHleDispatcher =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_DISABLE_RSP_TASK_HLE"), "1", StringComparison.Ordinal);
        private static readonly bool EnableRspInterpreter =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_INTERPRETER"), "0", StringComparison.Ordinal);
        private static readonly bool EnableRspInterpreterGraphicsOnly =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_INTERPRETER_GRAPHICS_ONLY"), "1", StringComparison.Ordinal);
        private static readonly bool AllowGraphicsRspHleFallback =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_GRAPHICS_HLE_FALLBACK"), "1", StringComparison.Ordinal);
        private static ulong _rspKickCount;
        private static int _traceWatchRangeLogCount;
        private static int _traceExceptionVectorWriteCount;
        private static int _traceLowRamMutationWriteCount;
        private static int _traceRspDmaWindowLogCount;
        private static int _traceRspDescriptorWriteLogCount;
        private static int _traceRdpSummaryCount;
        private static int _traceRdpTexRectCount;
        private static int _traceRdpTexRectWriteCount;
        private static int _traceRdpTextureLoadCount;
        private static int _traceRdpTriangleCount;
        private static int _traceRdpTriangleWriteCount;
        private static int _traceRdpFillRectCount;
        private static readonly ushort[] RdpZCompressTable = BuildRdpZCompressTable();
        private static readonly uint[] RdpZDecompressTable = BuildRdpZDecompressTable();
        private static readonly int[] RdpTextureCoordinateNormPointTable =
        {
            0x4000, 0x3f04, 0x3e10, 0x3d22, 0x3c3c, 0x3b5d, 0x3a83, 0x39b1,
            0x38e4, 0x381c, 0x375a, 0x369d, 0x35e5, 0x3532, 0x3483, 0x33d9,
            0x3333, 0x3291, 0x31f4, 0x3159, 0x30c3, 0x3030, 0x2fa1, 0x2f15,
            0x2e8c, 0x2e06, 0x2d83, 0x2d03, 0x2c86, 0x2c0b, 0x2b93, 0x2b1e,
            0x2aab, 0x2a3a, 0x29cc, 0x2960, 0x28f6, 0x288e, 0x2828, 0x27c4,
            0x2762, 0x2702, 0x26a4, 0x2648, 0x25ed, 0x2594, 0x253d, 0x24e7,
            0x2492, 0x243f, 0x23ee, 0x239e, 0x234f, 0x2302, 0x22b6, 0x226c,
            0x2222, 0x21da, 0x2193, 0x214d, 0x2108, 0x20c5, 0x2082, 0x2041
        };
        private static readonly int[] RdpTextureCoordinateNormSlopeTable =
        {
            0xf03, 0xf0b, 0xf11, 0xf19, 0xf20, 0xf25, 0xf2d, 0xf32,
            0xf37, 0xf3d, 0xf42, 0xf47, 0xf4c, 0xf50, 0xf55, 0xf59,
            0xf5d, 0xf62, 0xf64, 0xf69, 0xf6c, 0xf70, 0xf73, 0xf76,
            0xf79, 0xf7c, 0xf7f, 0xf82, 0xf84, 0xf87, 0xf8a, 0xf8c,
            0xf8e, 0xf91, 0xf93, 0xf95, 0xf97, 0xf99, 0xf9b, 0xf9d,
            0xf9f, 0xfa1, 0xfa3, 0xfa4, 0xfa6, 0xfa8, 0xfa9, 0xfaa,
            0xfac, 0xfae, 0xfaf, 0xfb0, 0xfb2, 0xfb3, 0xfb5, 0xfb5,
            0xfb7, 0xfb8, 0xfb9, 0xfba, 0xfbc, 0xfbc, 0xfbe, 0xfbe
        };
        private static readonly int[] RdpTextureCoordinateDivideTable = BuildRdpTextureCoordinateDivideTable();
        private const int TraceWatchRangeLogLimit = 512;
        private const int TraceExceptionVectorWriteLimit = 512;
        private const int TraceLowRamMutationWriteLimit = 1024;
        private const int TraceRspDmaWindowLogLimit = 128;
        private const int TraceRspDescriptorWriteLogLimit = 512;
        private const int TraceRdpSummaryLimit = 2048;
        private const int TraceRdpTexRectLimit = 128;
        private const int TraceRdpTexRectWriteLimit = 64;
        private const int TraceRdpTextureLoadLimit = 128;
        private const int TraceRdpTriangleLimit = 128;
        private const int TraceRdpTriangleWriteLimit = 64;
        private const int TraceRdpFillRectLimit = 64;
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
        private const uint RspDescriptorDmemStart = 0x00000410u;
        private const uint RspDescriptorDmemEnd = 0x00000428u;
        private const int FbInfosCount = 6;

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

        private static ushort[] BuildRdpZCompressTable()
        {
            ushort[] table = new ushort[0x40000];
            for (int z = 0; z < table.Length; z++)
            {
                int bucket = (z >> 11) & 0x7F;
                ushort encoded;
                if (bucket <= 0x3F)
                    encoded = (ushort)((z >> 4) & 0x1FFC);
                else if (bucket <= 0x5F)
                    encoded = (ushort)(((z >> 3) & 0x1FFC) | 0x2000);
                else if (bucket <= 0x6F)
                    encoded = (ushort)(((z >> 2) & 0x1FFC) | 0x4000);
                else if (bucket <= 0x77)
                    encoded = (ushort)(((z >> 1) & 0x1FFC) | 0x6000);
                else if (bucket <= 0x7B)
                    encoded = (ushort)((z & 0x1FFC) | 0x8000);
                else if (bucket <= 0x7D)
                    encoded = (ushort)(((z << 1) & 0x1FFC) | 0xA000);
                else if (bucket == 0x7E)
                    encoded = (ushort)(((z << 2) & 0x1FFC) | 0xC000);
                else
                    encoded = (ushort)(((z << 2) & 0x1FFC) | 0xE000);

                table[z] = encoded;
            }

            return table;
        }

        private static uint[] BuildRdpZDecompressTable()
        {
            uint[] table = new uint[0x4000];
            int[] shifts = { 6, 5, 4, 3, 2, 1, 0, 0 };
            uint[] adds = { 0x00000u, 0x20000u, 0x30000u, 0x38000u, 0x3C000u, 0x3E000u, 0x3F000u, 0x3F800u };
            for (int i = 0; i < table.Length; i++)
            {
                int exponent = (i >> 11) & 7;
                uint mantissa = (uint)(i & 0x7FF);
                table[i] = ((mantissa << shifts[exponent]) + adds[exponent]) & 0x3FFFFu;
            }

            return table;
        }

        private static int[] BuildRdpTextureCoordinateDivideTable()
        {
            int[] table = new int[0x8000];
            for (int i = 0; i < table.Length; i++)
            {
                int k;
                for (k = 1; k <= 14 && ((i << k) & 0x8000) == 0; k++)
                {
                }

                int shift = k - 1;
                int norm = (i << shift) & 0x3FFF;
                int wnorm = (norm & 0xFF) << 2;
                norm >>= 8;

                int point = RdpTextureCoordinateNormPointTable[norm];
                int slope = (RdpTextureCoordinateNormSlopeTable[norm] | ~0x3FF) + 1;
                int reciprocal = (((slope * wnorm) >> 10) + point) & 0x7FFF;
                table[i] = shift | (reciprocal << 4);
            }

            return table;
        }

        private static sbyte ParseSByteEnvironment(string name)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out int parsed))
                return 0;

            return (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, parsed));
        }

        private static ushort ParseN64ControllerButtons()
        {
            string raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_INPUT_HELD");
            if (string.IsNullOrWhiteSpace(raw))
                return 0;

            ushort buttons = 0;
            foreach (string rawToken in raw.Split(new[] { ',', ';', '|', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = rawToken.Trim().ToUpperInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
                switch (token)
                {
                    case "A": buttons |= 0x8000; break;
                    case "B": buttons |= 0x4000; break;
                    case "Z": buttons |= 0x2000; break;
                    case "START": buttons |= 0x1000; break;
                    case "UP":
                    case "DUP": buttons |= 0x0800; break;
                    case "DOWN":
                    case "DDOWN": buttons |= 0x0400; break;
                    case "LEFT":
                    case "DLEFT": buttons |= 0x0200; break;
                    case "RIGHT":
                    case "DRIGHT": buttons |= 0x0100; break;
                    case "L": buttons |= 0x0020; break;
                    case "R": buttons |= 0x0010; break;
                    case "CUP": buttons |= 0x0008; break;
                    case "CDOWN": buttons |= 0x0004; break;
                    case "CLEFT": buttons |= 0x0002; break;
                    case "CRIGHT": buttons |= 0x0001; break;
                }
            }

            return buttons;
        }

        private bool IsLateRspKickCpuPc()
        {
            uint pc = Registers.R4300.PC;
            return pc >= 0x800D6000u && pc <= 0x800D6200u;
        }

        private struct TrackedFramebufferInfo
        {
            public uint Addr;
            public uint Size;
            public uint Width;
            public uint Height;
            public uint SetEpoch;
            public uint WriteEpoch;
            public uint LastReadEpoch;
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

        private static bool IsRspDescriptorDmemAddress(uint spAddress, uint size = 1)
        {
            if ((spAddress & 0x1000u) != 0)
                return false;

            return RangeOverlaps(spAddress & 0x0FFFu, size, RspDescriptorDmemStart, RspDescriptorDmemEnd);
        }

        private void TraceRspDescriptorDmemWrite(uint spAddress, byte oldValue, byte newValue, string origin)
        {
            if ((!TraceN64Io && !TraceRspTaskDmem)
                || oldValue == newValue
                || _traceRspDescriptorWriteLogCount >= TraceRspDescriptorWriteLogLimit)
                return;

            _traceRspDescriptorWriteLogCount++;
            Common.Logger.PrintWarningLine(
                $"[N64RSPDMEMWRITE] origin={origin} pc=0x{Registers.R4300.PC:x8} rspPc=0x{ReadRspPc():x3} " +
                $"addr=0x{(spAddress & 0x0FFFu):x3} old=0x{oldValue:x2} new=0x{newValue:x2} " +
                $"spStatus=0x{ReadBigEndianWord(SP_STATUS_REG_R):x8} spMem=0x{ReadBigEndianWord(SP_MEM_ADDR_REG_RW):x8} " +
                $"spDram=0x{ReadBigEndianWord(SP_DRAM_ADDR_REG_RW):x8} rdLen=0x{ReadBigEndianWord(SP_RD_LEN_REG_RW):x8}");
        }

        private void TraceRspDescriptorDmemSnapshot(string tag)
        {
            if ((!TraceN64Io && !TraceRspTaskDmem)
                || _traceRspDescriptorWriteLogCount >= TraceRspDescriptorWriteLogLimit)
                return;

            _traceRspDescriptorWriteLogCount++;
            Common.Logger.PrintWarningLine(
                $"[N64RSPDMEMDESC] tag={tag} pc=0x{Registers.R4300.PC:x8} rspPc=0x{ReadRspPc():x3} " +
                $"d410=0x{ReadSpMemoryWordBigEndian(0x410):x8} d414=0x{ReadSpMemoryWordBigEndian(0x414):x8} " +
                $"d418=0x{ReadSpMemoryWordBigEndian(0x418):x8} d41c=0x{ReadSpMemoryWordBigEndian(0x41c):x8} " +
                $"d420=0x{ReadSpMemoryWordBigEndian(0x420):x8} d424=0x{ReadSpMemoryWordBigEndian(0x424):x8} " +
                $"d428=0x{ReadSpMemoryWordBigEndian(0x428):x8}");
        }

        private uint ReadSpMemoryWordBigEndian(uint spAddress)
        {
            uint aligned = spAddress & 0x1FFCu;
            return (uint)((ReadSpMemoryByte(aligned) << 24)
                | (ReadSpMemoryByte(aligned + 1) << 16)
                | (ReadSpMemoryByte(aligned + 2) << 8)
                | ReadSpMemoryByte(aligned + 3));
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

        public readonly byte[] SP_MEM_RW          = new byte[0x2000];
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
        public byte RomCountryCode => _rom != null && _rom.Length > 0x3Eu ? _rom[0x3E] : (byte)0;
        private uint _openBusMissCount;
        private bool _piDmaBusy;
        private bool _piInterruptDelayArmed;
        private uint _piInterruptDelayRemaining;
        private uint _piIrqRaiseCount;
        private uint _piIrqClearCount;
        private uint _cartridgeBusLastWriteWord;
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
        private uint _fbInfoEpoch;
        private uint _rdramWriteEpoch;
        private uint _lastViOriginWriteValue;
        private uint _lastViOriginWritePc;
        private uint _lastPlausibleViOriginWriteValue;
        private uint _lastPlausibleViOriginWritePc;
        private bool _warnedRspTaskHle;
        private bool _warnedRspInterpreterFallback;
        private bool _warnedRspGraphicsFailLoud;
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
        private bool _dpCompletionPending;
        private RspTask _activeRspTask;
        private readonly RspInterpreter _rspInterpreter;
        private uint _rdpColorImageAddress;
        private uint _rdpColorImageWidth;
        private uint _rdpColorImageSize;
        private uint _rdpFillColor;
        private uint _rdpPrimColor = 0xFFFFFFFFu;
        private uint _rdpEnvColor = 0xFFFFFFFFu;
        private uint _rdpBlendColor = 0xFFFFFFFFu;
        private uint _rdpFogColor = 0x000000FFu;
        private uint _rdpMaskImageAddress;
        private uint _rdpPrimitiveDepth = 0x3FFFFu;
        private uint _rdpPrimitiveDeltaZ = 1u;
        private int _rdpScissorX0;
        private int _rdpScissorY0;
        private int _rdpScissorX1 = int.MaxValue;
        private int _rdpScissorY1 = int.MaxValue;
        private uint _rdpOtherModesCycleType;
        private bool _rdpOtherModesEnableTlut;
        private bool _rdpOtherModesTlutType;
        private bool _rdpOtherModesPerspectiveTexture;
        private uint _rdpOtherModesZMode;
        private bool _rdpOtherModesZUpdate;
        private bool _rdpOtherModesZCompare;
        private bool _rdpOtherModesZSourceSel;
        private bool _rdpOtherModesAlphaCompare;
        private bool _rdpOtherModesForceBlend;
        private bool _rdpOtherModesImageRead;
        private uint _rdpOtherModesCvgDest;
        private bool _rdpCombineModeSet;
        private RdpCombineMode _rdpCombine;
        private uint _rdpTextureImageAddress;
        private uint _rdpTextureImageWidth;
        private uint _rdpTextureImageSize;
        private uint _rdpTextureImageFormat;
        private readonly RdpTileState[] _rdpTiles = new RdpTileState[8];
        private readonly byte[] _rdpTmem = new byte[4096];
        private readonly ushort[] _rdpTlut = new ushort[256];
        private readonly byte[] _rdpHiddenBits = new byte[8388608 / 2];
        private uint _lastRdpColorImageAddress;
        private uint _lastRdpColorImageWidth;
        private uint _lastRdpColorImageBytesPerPixel;
        private uint _lastRdpColorImageWriteEpoch;
        private readonly object _lastVisibleRdpFramebufferLock = new object();
        private byte[] _lastVisibleRdpFramebufferSnapshot = Array.Empty<byte>();
        private uint _lastVisibleRdpFramebufferAddress;
        private uint _lastVisibleRdpFramebufferWidth;
        private uint _lastVisibleRdpFramebufferHeight;
        private uint _lastVisibleRdpFramebufferBytesPerPixel;
        private uint _lastVisibleRdpFramebufferEpoch;
        private const int RdpVisibleFramebufferSnapshotSlots = 4;
        private readonly byte[][] _visibleRdpFramebufferSnapshots = new byte[RdpVisibleFramebufferSnapshotSlots][];
        private readonly uint[] _visibleRdpFramebufferAddresses = new uint[RdpVisibleFramebufferSnapshotSlots];
        private readonly uint[] _visibleRdpFramebufferWidths = new uint[RdpVisibleFramebufferSnapshotSlots];
        private readonly uint[] _visibleRdpFramebufferHeights = new uint[RdpVisibleFramebufferSnapshotSlots];
        private readonly uint[] _visibleRdpFramebufferBytesPerPixels = new uint[RdpVisibleFramebufferSnapshotSlots];
        private readonly uint[] _visibleRdpFramebufferEpochs = new uint[RdpVisibleFramebufferSnapshotSlots];
        private int _visibleRdpFramebufferSnapshotCursor;
        private long _rspGraphicsTaskCount;
        private long _rspAudioTaskCount;
        private long _rspOtherTaskCount;
        private long _rdpDisplayListCount;
        private long _rdpCommandCount;
        private long _rdpHandledCommandCount;
        private long _rdpSetColorImageCommandCount;
        private long _rdpTriangleCommandCount;
        private long _rdpTextureRectangleCommandCount;
        private long _rdpFillRectangleCommandCount;
        private long _rdpPixelWriteCount;
        private long _rdpNonZeroPixelWriteCount;
        private long _perfRdpDisplayListTicks;
        private long _perfRdpDisplayListCalls;
        private long _perfRdpTexturedTriangleTicks;
        private long _perfRdpTexturedTriangleCalls;
        private long _perfRdpTexturedRectangleTicks;
        private long _perfRdpTexturedRectangleCalls;
        private long _perfRdpSnapshotTicks;
        private long _perfRdpSnapshotCalls;
        private long _perfRdpSnapshotBytes;
        private long _perfRdpVisibleScanTicks;
        private long _perfRdpVisibleScanCalls;
        private long _perfRdpVisibleScanPixels;

        private struct RdpTileState
        {
            public uint Format;
            public uint Size;
            public uint Line;
            public uint Tmem;
            public uint Palette;
            public uint MaskS;
            public uint MaskT;
            public uint ShiftS;
            public uint ShiftT;
            public bool ClampS;
            public bool ClampT;
            public bool MirrorS;
            public bool MirrorT;
            public uint Uls;
            public uint Ult;
            public uint Lrs;
            public uint Lrt;
            public bool TileSizeSet;
        }

        private struct RdpCombineMode
        {
            public int SubARgb0;
            public int SubBRgb0;
            public int MulRgb0;
            public int AddRgb0;
            public int SubAA0;
            public int SubBA0;
            public int MulA0;
            public int AddA0;
            public int SubARgb1;
            public int SubBRgb1;
            public int MulRgb1;
            public int AddRgb1;
            public int SubAA1;
            public int SubBA1;
            public int MulA1;
            public int AddA1;
        }

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
        private readonly byte[] _fbDirtyPage = new byte[RdramPageCount];
        private readonly TrackedFramebufferInfo[] _fbInfos = new TrackedFramebufferInfo[FbInfosCount];
        private readonly uint[] _rdramPageLastWriteEpoch = new uint[RdramPageCount];

        public uint LastViOriginWriteValue => _lastViOriginWriteValue;
        public uint LastViOriginWritePc => _lastViOriginWritePc;
        public uint LastPlausibleViOriginWriteValue => _lastPlausibleViOriginWriteValue;
        public uint LastPlausibleViOriginWritePc => _lastPlausibleViOriginWritePc;
        public uint LastRdpColorImageAddress => Volatile.Read(ref _lastRdpColorImageAddress);
        public uint LastRdpColorImageWidth => Volatile.Read(ref _lastRdpColorImageWidth);
        public uint LastRdpColorImageBytesPerPixel => Volatile.Read(ref _lastRdpColorImageBytesPerPixel);
        public uint LastRdpColorImageWriteEpoch => Volatile.Read(ref _lastRdpColorImageWriteEpoch);
        public long RspGraphicsTaskCount => Volatile.Read(ref _rspGraphicsTaskCount);
        public long RspAudioTaskCount => Volatile.Read(ref _rspAudioTaskCount);
        public long RspOtherTaskCount => Volatile.Read(ref _rspOtherTaskCount);
        public long RdpDisplayListCount => Volatile.Read(ref _rdpDisplayListCount);
        public long RdpCommandCount => Volatile.Read(ref _rdpCommandCount);
        public long RdpHandledCommandCount => Volatile.Read(ref _rdpHandledCommandCount);
        public long RdpSetColorImageCommandCount => Volatile.Read(ref _rdpSetColorImageCommandCount);
        public long RdpTriangleCommandCount => Volatile.Read(ref _rdpTriangleCommandCount);
        public long RdpTextureRectangleCommandCount => Volatile.Read(ref _rdpTextureRectangleCommandCount);
        public long RdpFillRectangleCommandCount => Volatile.Read(ref _rdpFillRectangleCommandCount);
        public long RdpPixelWriteCount => Volatile.Read(ref _rdpPixelWriteCount);
        public long RdpNonZeroPixelWriteCount => Volatile.Read(ref _rdpNonZeroPixelWriteCount);
        public string PerformanceSummary
        {
            get
            {
                if (!EnableN64Perf)
                    return "perf=off";

                long displayListTicks = Volatile.Read(ref _perfRdpDisplayListTicks);
                long displayListCalls = Volatile.Read(ref _perfRdpDisplayListCalls);
                long triTicks = Volatile.Read(ref _perfRdpTexturedTriangleTicks);
                long triCalls = Volatile.Read(ref _perfRdpTexturedTriangleCalls);
                long rectTicks = Volatile.Read(ref _perfRdpTexturedRectangleTicks);
                long rectCalls = Volatile.Read(ref _perfRdpTexturedRectangleCalls);
                long snapshotTicks = Volatile.Read(ref _perfRdpSnapshotTicks);
                long snapshotCalls = Volatile.Read(ref _perfRdpSnapshotCalls);
                long snapshotBytes = Volatile.Read(ref _perfRdpSnapshotBytes);
                long scanTicks = Volatile.Read(ref _perfRdpVisibleScanTicks);
                long scanCalls = Volatile.Read(ref _perfRdpVisibleScanCalls);
                long scanPixels = Volatile.Read(ref _perfRdpVisibleScanPixels);

                return
                    $"rdpList={displayListCalls}/{TicksToMs(displayListTicks):0.###}ms avg={AvgTicksMs(displayListTicks, displayListCalls):0.###}ms " +
                    $"triTex={triCalls}/{TicksToMs(triTicks):0.###}ms avg={AvgTicksMs(triTicks, triCalls):0.###}ms " +
                    $"texRect={rectCalls}/{TicksToMs(rectTicks):0.###}ms avg={AvgTicksMs(rectTicks, rectCalls):0.###}ms " +
                    $"fbScan={scanCalls}/{TicksToMs(scanTicks):0.###}ms avg={AvgTicksMs(scanTicks, scanCalls):0.###}ms px={scanPixels} " +
                    $"fbSnap={snapshotCalls}/{TicksToMs(snapshotTicks):0.###}ms avg={AvgTicksMs(snapshotTicks, snapshotCalls):0.###}ms bytes={snapshotBytes}";
            }
        }

        public void SaveState(BinaryWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            const int version = 1;
            writer.Write(version);

            WriteByteArrays(writer);
            WriteBytes(writer, SI_MIRROR_RAM);
            WriteBytes(writer, OpenBus);
            WriteBytes(writer, _rdpTmem);
            WriteUshortArray(writer, _rdpTlut);
            WriteBytes(writer, _rdpHiddenBits);
            WriteBytes(writer, _fbDirtyPage);
            WriteUintArray(writer, _rdramPageLastWriteEpoch);
            WriteFramebufferInfos(writer);

            writer.Write(_openBusMissCount);
            writer.Write(_piDmaBusy);
            writer.Write(_piInterruptDelayArmed);
            writer.Write(_piInterruptDelayRemaining);
            writer.Write(_piIrqRaiseCount);
            writer.Write(_piIrqClearCount);
            writer.Write(_cartridgeBusLastWriteWord);
            writer.Write(_siDmaActive);
            writer.Write(_siDmaReadToDram);
            writer.Write(_siDramAddr);
            writer.Write(_siDirectPifWriteActive);
            writer.Write(_siInterruptDelayArmed);
            writer.Write(_siInterruptDelayRemaining);
            writer.Write(_aiFifo0Address);
            writer.Write(_aiFifo0Length);
            writer.Write(_aiFifo0Duration);
            writer.Write(_aiFifo1Address);
            writer.Write(_aiFifo1Length);
            writer.Write(_aiFifo1Duration);
            writer.Write(_aiInterruptDelayArmed);
            writer.Write(_aiInterruptDelayRemaining);
            writer.Write(_aiDelayedCarry);
            writer.Write(_viCurrentLine);
            writer.Write(_viLineCycleAccum);
            writer.Write(_viFrameDelayCycles);
            writer.Write(_viInterruptCyclesRemaining);
            writer.Write(_viField);
            writer.Write(_fbInfoEpoch);
            writer.Write(_rdramWriteEpoch);
            writer.Write(_lastViOriginWriteValue);
            writer.Write(_lastViOriginWritePc);
            writer.Write(_lastPlausibleViOriginWriteValue);
            writer.Write(_lastPlausibleViOriginWritePc);
            writer.Write(_warnedRspTaskHle);
            writer.Write(_warnedRspInterpreterFallback);
            writer.Write(_warnedRspGraphicsFailLoud);
            writer.Write(_spDmaBusy);
            writer.Write(_spDmaFull);
            writer.Write(_spDmaDelayArmed);
            writer.Write(_spDmaDelayRemaining);
            WriteSpDmaRequest(writer, _spQueuedDma);
            writer.Write(_spQueuedDmaValid);
            writer.Write(_rspTaskActive);
            writer.Write(_rspTaskDispatching);
            writer.Write(_rspTaskCyclesRemaining);
            writer.Write(_rspInterruptDelayRemaining);
            writer.Write(_rspInterruptDelayArmed);
            writer.Write(_rspTaskLocked);
            writer.Write(_activeRspTracePc);
            writer.Write(_hasActiveRspTracePc);
            writer.Write(_dpInterruptDelayRemaining);
            writer.Write(_dpInterruptDelayArmed);
            writer.Write(_dpCompletionPending);
            WriteRspTask(writer, _activeRspTask);

            writer.Write(_rdpColorImageAddress);
            writer.Write(_rdpColorImageWidth);
            writer.Write(_rdpColorImageSize);
            writer.Write(_rdpFillColor);
            writer.Write(_rdpPrimColor);
            writer.Write(_rdpEnvColor);
            writer.Write(_rdpBlendColor);
            writer.Write(_rdpFogColor);
            writer.Write(_rdpMaskImageAddress);
            writer.Write(_rdpPrimitiveDepth);
            writer.Write(_rdpPrimitiveDeltaZ);
            writer.Write(_rdpScissorX0);
            writer.Write(_rdpScissorY0);
            writer.Write(_rdpScissorX1);
            writer.Write(_rdpScissorY1);
            writer.Write(_rdpOtherModesCycleType);
            writer.Write(_rdpOtherModesEnableTlut);
            writer.Write(_rdpOtherModesTlutType);
            writer.Write(_rdpOtherModesPerspectiveTexture);
            writer.Write(_rdpOtherModesZMode);
            writer.Write(_rdpOtherModesZUpdate);
            writer.Write(_rdpOtherModesZCompare);
            writer.Write(_rdpOtherModesZSourceSel);
            writer.Write(_rdpOtherModesAlphaCompare);
            writer.Write(_rdpOtherModesForceBlend);
            writer.Write(_rdpOtherModesImageRead);
            writer.Write(_rdpOtherModesCvgDest);
            writer.Write(_rdpCombineModeSet);
            WriteRdpCombine(writer, _rdpCombine);
            writer.Write(_rdpTextureImageAddress);
            writer.Write(_rdpTextureImageWidth);
            writer.Write(_rdpTextureImageSize);
            writer.Write(_rdpTextureImageFormat);
            WriteRdpTiles(writer);
            writer.Write(_lastRdpColorImageAddress);
            writer.Write(_lastRdpColorImageWidth);
            writer.Write(_lastRdpColorImageBytesPerPixel);
            writer.Write(_lastRdpColorImageWriteEpoch);
            lock (_lastVisibleRdpFramebufferLock)
            {
                WriteBytes(writer, _lastVisibleRdpFramebufferSnapshot);
                writer.Write(_lastVisibleRdpFramebufferAddress);
                writer.Write(_lastVisibleRdpFramebufferWidth);
                writer.Write(_lastVisibleRdpFramebufferHeight);
                writer.Write(_lastVisibleRdpFramebufferBytesPerPixel);
                writer.Write(_lastVisibleRdpFramebufferEpoch);
            }

            writer.Write(_rspGraphicsTaskCount);
            writer.Write(_rspAudioTaskCount);
            writer.Write(_rspOtherTaskCount);
            writer.Write(_rdpDisplayListCount);
            writer.Write(_rdpCommandCount);
            writer.Write(_rdpHandledCommandCount);
            writer.Write(_rdpSetColorImageCommandCount);
            writer.Write(_rdpTriangleCommandCount);
            writer.Write(_rdpTextureRectangleCommandCount);
            writer.Write(_rdpFillRectangleCommandCount);
            writer.Write(_rdpPixelWriteCount);
            writer.Write(_rdpNonZeroPixelWriteCount);
        }

        public void LoadState(BinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported N64 memory savestate version: {version}.");

            ReadByteArrays(reader);
            ReadBytes(reader, SI_MIRROR_RAM);
            ReadBytes(reader, OpenBus);
            ReadBytes(reader, _rdpTmem);
            ReadUshortArray(reader, _rdpTlut);
            ReadBytes(reader, _rdpHiddenBits);
            ReadBytes(reader, _fbDirtyPage);
            ReadUintArray(reader, _rdramPageLastWriteEpoch);
            ReadFramebufferInfos(reader);

            _openBusMissCount = reader.ReadUInt32();
            _piDmaBusy = reader.ReadBoolean();
            _piInterruptDelayArmed = reader.ReadBoolean();
            _piInterruptDelayRemaining = reader.ReadUInt32();
            _piIrqRaiseCount = reader.ReadUInt32();
            _piIrqClearCount = reader.ReadUInt32();
            _cartridgeBusLastWriteWord = reader.ReadUInt32();
            _siDmaActive = reader.ReadBoolean();
            _siDmaReadToDram = reader.ReadBoolean();
            _siDramAddr = reader.ReadUInt32();
            _siDirectPifWriteActive = reader.ReadBoolean();
            _siInterruptDelayArmed = reader.ReadBoolean();
            _siInterruptDelayRemaining = reader.ReadUInt32();
            _aiFifo0Address = reader.ReadUInt32();
            _aiFifo0Length = reader.ReadUInt32();
            _aiFifo0Duration = reader.ReadUInt32();
            _aiFifo1Address = reader.ReadUInt32();
            _aiFifo1Length = reader.ReadUInt32();
            _aiFifo1Duration = reader.ReadUInt32();
            _aiInterruptDelayArmed = reader.ReadBoolean();
            _aiInterruptDelayRemaining = reader.ReadUInt32();
            _aiDelayedCarry = reader.ReadBoolean();
            _viCurrentLine = reader.ReadUInt32();
            _viLineCycleAccum = reader.ReadUInt32();
            _viFrameDelayCycles = reader.ReadUInt32();
            _viInterruptCyclesRemaining = reader.ReadUInt32();
            _viField = reader.ReadUInt32();
            _fbInfoEpoch = reader.ReadUInt32();
            _rdramWriteEpoch = reader.ReadUInt32();
            _lastViOriginWriteValue = reader.ReadUInt32();
            _lastViOriginWritePc = reader.ReadUInt32();
            _lastPlausibleViOriginWriteValue = reader.ReadUInt32();
            _lastPlausibleViOriginWritePc = reader.ReadUInt32();
            _warnedRspTaskHle = reader.ReadBoolean();
            _warnedRspInterpreterFallback = reader.ReadBoolean();
            _warnedRspGraphicsFailLoud = reader.ReadBoolean();
            _spDmaBusy = reader.ReadBoolean();
            _spDmaFull = reader.ReadBoolean();
            _spDmaDelayArmed = reader.ReadBoolean();
            _spDmaDelayRemaining = reader.ReadUInt32();
            _spQueuedDma = ReadSpDmaRequest(reader);
            _spQueuedDmaValid = reader.ReadBoolean();
            _rspTaskActive = reader.ReadBoolean();
            _rspTaskDispatching = reader.ReadBoolean();
            _rspTaskCyclesRemaining = reader.ReadUInt32();
            _rspInterruptDelayRemaining = reader.ReadUInt32();
            _rspInterruptDelayArmed = reader.ReadBoolean();
            _rspTaskLocked = reader.ReadBoolean();
            _activeRspTracePc = reader.ReadUInt32();
            _hasActiveRspTracePc = reader.ReadBoolean();
            _writeUInt8Origin = null;
            _dpInterruptDelayRemaining = reader.ReadUInt32();
            _dpInterruptDelayArmed = reader.ReadBoolean();
            _dpCompletionPending = reader.ReadBoolean();
            _activeRspTask = ReadRspTask(reader);

            _rdpColorImageAddress = reader.ReadUInt32();
            _rdpColorImageWidth = reader.ReadUInt32();
            _rdpColorImageSize = reader.ReadUInt32();
            _rdpFillColor = reader.ReadUInt32();
            _rdpPrimColor = reader.ReadUInt32();
            _rdpEnvColor = reader.ReadUInt32();
            _rdpBlendColor = reader.ReadUInt32();
            _rdpFogColor = reader.ReadUInt32();
            _rdpMaskImageAddress = reader.ReadUInt32();
            _rdpPrimitiveDepth = reader.ReadUInt32();
            _rdpPrimitiveDeltaZ = reader.ReadUInt32();
            _rdpScissorX0 = reader.ReadInt32();
            _rdpScissorY0 = reader.ReadInt32();
            _rdpScissorX1 = reader.ReadInt32();
            _rdpScissorY1 = reader.ReadInt32();
            _rdpOtherModesCycleType = reader.ReadUInt32();
            _rdpOtherModesEnableTlut = reader.ReadBoolean();
            _rdpOtherModesTlutType = reader.ReadBoolean();
            _rdpOtherModesPerspectiveTexture = reader.ReadBoolean();
            _rdpOtherModesZMode = reader.ReadUInt32();
            _rdpOtherModesZUpdate = reader.ReadBoolean();
            _rdpOtherModesZCompare = reader.ReadBoolean();
            _rdpOtherModesZSourceSel = reader.ReadBoolean();
            _rdpOtherModesAlphaCompare = reader.ReadBoolean();
            _rdpOtherModesForceBlend = reader.ReadBoolean();
            _rdpOtherModesImageRead = reader.ReadBoolean();
            _rdpOtherModesCvgDest = reader.ReadUInt32();
            _rdpCombineModeSet = reader.ReadBoolean();
            _rdpCombine = ReadRdpCombine(reader);
            _rdpTextureImageAddress = reader.ReadUInt32();
            _rdpTextureImageWidth = reader.ReadUInt32();
            _rdpTextureImageSize = reader.ReadUInt32();
            _rdpTextureImageFormat = reader.ReadUInt32();
            ReadRdpTiles(reader);
            _lastRdpColorImageAddress = reader.ReadUInt32();
            _lastRdpColorImageWidth = reader.ReadUInt32();
            _lastRdpColorImageBytesPerPixel = reader.ReadUInt32();
            _lastRdpColorImageWriteEpoch = reader.ReadUInt32();
            lock (_lastVisibleRdpFramebufferLock)
            {
                _lastVisibleRdpFramebufferSnapshot = ReadNewByteArray(reader);
                _lastVisibleRdpFramebufferAddress = reader.ReadUInt32();
                _lastVisibleRdpFramebufferWidth = reader.ReadUInt32();
                _lastVisibleRdpFramebufferHeight = reader.ReadUInt32();
                _lastVisibleRdpFramebufferBytesPerPixel = reader.ReadUInt32();
                _lastVisibleRdpFramebufferEpoch = reader.ReadUInt32();
                ResetVisibleRdpFramebufferSnapshotCacheLocked();
                if (_lastVisibleRdpFramebufferSnapshot.Length != 0
                    && _lastVisibleRdpFramebufferAddress != 0
                    && _lastVisibleRdpFramebufferEpoch != 0)
                {
                    StoreVisibleRdpFramebufferSnapshotLocked(
                        _lastVisibleRdpFramebufferSnapshot,
                        _lastVisibleRdpFramebufferAddress,
                        _lastVisibleRdpFramebufferWidth,
                        _lastVisibleRdpFramebufferHeight,
                        _lastVisibleRdpFramebufferBytesPerPixel,
                        _lastVisibleRdpFramebufferEpoch);
                }
            }

            _rspGraphicsTaskCount = reader.ReadInt64();
            _rspAudioTaskCount = reader.ReadInt64();
            _rspOtherTaskCount = reader.ReadInt64();
            _rdpDisplayListCount = reader.ReadInt64();
            _rdpCommandCount = reader.ReadInt64();
            _rdpHandledCommandCount = reader.ReadInt64();
            _rdpSetColorImageCommandCount = reader.ReadInt64();
            _rdpTriangleCommandCount = reader.ReadInt64();
            _rdpTextureRectangleCommandCount = reader.ReadInt64();
            _rdpFillRectangleCommandCount = reader.ReadInt64();
            _rdpPixelWriteCount = reader.ReadInt64();
            _rdpNonZeroPixelWriteCount = reader.ReadInt64();

            RefreshCpuInterruptView();
        }

        private static long StartPerfTimer()
        {
            return EnableN64Perf ? Stopwatch.GetTimestamp() : 0;
        }

        private static void AddPerfTicks(ref long ticksField, ref long callsField, long start)
        {
            if (start == 0)
                return;

            Interlocked.Add(ref ticksField, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref callsField);
        }

        private void WriteByteArrays(BinaryWriter writer)
        {
            WriteBytes(writer, SP_MEM_RW);
            WriteBytes(writer, SP_MEM_ADDR_REG_RW);
            WriteBytes(writer, SP_DRAM_ADDR_REG_RW);
            WriteBytes(writer, SP_RD_LEN_REG_RW);
            WriteBytes(writer, SP_WR_LEN_REG_RW);
            WriteBytes(writer, SP_STATUS_REG_R);
            WriteBytes(writer, SP_STATUS_REG_W);
            WriteBytes(writer, SP_DMA_FULL_REG_R);
            WriteBytes(writer, SP_DMA_FULL_REG_W);
            WriteBytes(writer, SP_DMA_BUSY_REG_R);
            WriteBytes(writer, SP_DMA_BUSY_REG_W);
            WriteBytes(writer, SP_SEMAPHORE_REG_R);
            WriteBytes(writer, SP_SEMAPHORE_REG_W);
            WriteBytes(writer, SP_PC_REG_RW);
            WriteBytes(writer, SP_IBIST_REG_RW);
            WriteBytes(writer, DPC_START_REG_RW);
            WriteBytes(writer, DPC_END_REG_RW);
            WriteBytes(writer, DPC_CURRENT_REG_RW);
            WriteBytes(writer, DPC_STATUS_REG_R);
            WriteBytes(writer, DPC_STATUS_REG_W);
            WriteBytes(writer, DPC_CLOCK_REG_RW);
            WriteBytes(writer, DPC_BUFBUSY_REG_RW);
            WriteBytes(writer, DPC_PIPEBUSY_REG_RW);
            WriteBytes(writer, DPC_TMEM_REG_RW);
            WriteBytes(writer, DPS_TBIST_REG_RW);
            WriteBytes(writer, DPS_TEST_MODE_REG_RW);
            WriteBytes(writer, DPS_BUFTEST_ADDR_REG_RW);
            WriteBytes(writer, DPS_BUFTEST_DATA_REG_RW);
            WriteBytes(writer, MI_INIT_MODE_REG_R);
            WriteBytes(writer, MI_INIT_MODE_REG_W);
            WriteBytes(writer, MI_VERSION_REG_RW);
            WriteBytes(writer, MI_INTR_REG_R);
            WriteBytes(writer, MI_INTR_MASK_REG_R);
            WriteBytes(writer, MI_INTR_MASK_REG_W);
            WriteBytes(writer, VI_STATUS_REG_RW);
            WriteBytes(writer, VI_ORIGIN_REG_RW);
            WriteBytes(writer, VI_WIDTH_REG_RW);
            WriteBytes(writer, VI_INTR_REG_RW);
            WriteBytes(writer, VI_CURRENT_REG_RW);
            WriteBytes(writer, VI_BURST_REG_RW);
            WriteBytes(writer, VI_V_SYNC_REG_RW);
            WriteBytes(writer, VI_H_SYNC_REG_RW);
            WriteBytes(writer, VI_LEAP_REG_RW);
            WriteBytes(writer, VI_H_START_REG_RW);
            WriteBytes(writer, VI_V_START_REG_RW);
            WriteBytes(writer, VI_V_BURST_REG_RW);
            WriteBytes(writer, VI_X_SCALE_REG_RW);
            WriteBytes(writer, VI_Y_SCALE_REG_RW);
            WriteBytes(writer, AI_DRAM_ADDR_REG_W);
            WriteBytes(writer, AI_LEN_REG_RW);
            WriteBytes(writer, AI_CONTROL_REG_W);
            WriteBytes(writer, AI_STATUS_REG_R);
            WriteBytes(writer, AI_STATUS_REG_W);
            WriteBytes(writer, AI_DACRATE_REG_W);
            WriteBytes(writer, AI_BITRATE_REG_W);
            WriteBytes(writer, PI_DRAM_ADDR_REG_RW);
            WriteBytes(writer, PI_CART_ADDR_REG_RW);
            WriteBytes(writer, PI_RD_LEN_REG_RW);
            WriteBytes(writer, PI_WR_LEN_REG_RW);
            WriteBytes(writer, PI_STATUS_REG_R);
            WriteBytes(writer, PI_STATUS_REG_W);
            WriteBytes(writer, PI_BSD_DOM1_LAT_REG_RW);
            WriteBytes(writer, PI_BSD_DOM1_PWD_REG_RW);
            WriteBytes(writer, PI_BSD_DOM1_PGS_REG_RW);
            WriteBytes(writer, PI_BSD_DOM1_RLS_REG_RW);
            WriteBytes(writer, PI_BSD_DOM2_LAT_REG_RW);
            WriteBytes(writer, PI_BSD_DOM2_PWD_REG_RW);
            WriteBytes(writer, PI_BSD_DOM2_PGS_REG_RW);
            WriteBytes(writer, PI_BSD_DOM2_RLS_REG_RW);
            WriteBytes(writer, SI_DRAM_ADDR_REG_RW);
            WriteBytes(writer, SI_PIF_ADDR_RD64B_REG_RW);
            WriteBytes(writer, SI_PIF_ADDR_WR64B_REG_RW);
            WriteBytes(writer, SI_STATUS_REG_R);
            WriteBytes(writer, SI_STATUS_REG_W);
            WriteBytes(writer, RI_MODE_REG_RW);
            WriteBytes(writer, RI_CONFIG_REG_RW);
            WriteBytes(writer, RI_CURRENT_LOAD_REG_RW);
            WriteBytes(writer, RI_SELECT_REG_RW);
            WriteBytes(writer, RI_REFRESH_REG_RW);
            WriteBytes(writer, RI_LATENCY_REG_RW);
            WriteBytes(writer, RI_ERROR_REG_RW);
            WriteBytes(writer, RI_WERROR_REG_RW);
            WriteBytes(writer, RDRAM);
            WriteBytes(writer, RDRAMReg);
            WriteBytes(writer, PIFROM);
            WriteBytes(writer, PIFRAM);
        }

        private void ReadByteArrays(BinaryReader reader)
        {
            ReadBytes(reader, SP_MEM_RW);
            ReadBytes(reader, SP_MEM_ADDR_REG_RW);
            ReadBytes(reader, SP_DRAM_ADDR_REG_RW);
            ReadBytes(reader, SP_RD_LEN_REG_RW);
            ReadBytes(reader, SP_WR_LEN_REG_RW);
            ReadBytes(reader, SP_STATUS_REG_R);
            ReadBytes(reader, SP_STATUS_REG_W);
            ReadBytes(reader, SP_DMA_FULL_REG_R);
            ReadBytes(reader, SP_DMA_FULL_REG_W);
            ReadBytes(reader, SP_DMA_BUSY_REG_R);
            ReadBytes(reader, SP_DMA_BUSY_REG_W);
            ReadBytes(reader, SP_SEMAPHORE_REG_R);
            ReadBytes(reader, SP_SEMAPHORE_REG_W);
            ReadBytes(reader, SP_PC_REG_RW);
            ReadBytes(reader, SP_IBIST_REG_RW);
            ReadBytes(reader, DPC_START_REG_RW);
            ReadBytes(reader, DPC_END_REG_RW);
            ReadBytes(reader, DPC_CURRENT_REG_RW);
            ReadBytes(reader, DPC_STATUS_REG_R);
            ReadBytes(reader, DPC_STATUS_REG_W);
            ReadBytes(reader, DPC_CLOCK_REG_RW);
            ReadBytes(reader, DPC_BUFBUSY_REG_RW);
            ReadBytes(reader, DPC_PIPEBUSY_REG_RW);
            ReadBytes(reader, DPC_TMEM_REG_RW);
            ReadBytes(reader, DPS_TBIST_REG_RW);
            ReadBytes(reader, DPS_TEST_MODE_REG_RW);
            ReadBytes(reader, DPS_BUFTEST_ADDR_REG_RW);
            ReadBytes(reader, DPS_BUFTEST_DATA_REG_RW);
            ReadBytes(reader, MI_INIT_MODE_REG_R);
            ReadBytes(reader, MI_INIT_MODE_REG_W);
            ReadBytes(reader, MI_VERSION_REG_RW);
            ReadBytes(reader, MI_INTR_REG_R);
            ReadBytes(reader, MI_INTR_MASK_REG_R);
            ReadBytes(reader, MI_INTR_MASK_REG_W);
            ReadBytes(reader, VI_STATUS_REG_RW);
            ReadBytes(reader, VI_ORIGIN_REG_RW);
            ReadBytes(reader, VI_WIDTH_REG_RW);
            ReadBytes(reader, VI_INTR_REG_RW);
            ReadBytes(reader, VI_CURRENT_REG_RW);
            ReadBytes(reader, VI_BURST_REG_RW);
            ReadBytes(reader, VI_V_SYNC_REG_RW);
            ReadBytes(reader, VI_H_SYNC_REG_RW);
            ReadBytes(reader, VI_LEAP_REG_RW);
            ReadBytes(reader, VI_H_START_REG_RW);
            ReadBytes(reader, VI_V_START_REG_RW);
            ReadBytes(reader, VI_V_BURST_REG_RW);
            ReadBytes(reader, VI_X_SCALE_REG_RW);
            ReadBytes(reader, VI_Y_SCALE_REG_RW);
            ReadBytes(reader, AI_DRAM_ADDR_REG_W);
            ReadBytes(reader, AI_LEN_REG_RW);
            ReadBytes(reader, AI_CONTROL_REG_W);
            ReadBytes(reader, AI_STATUS_REG_R);
            ReadBytes(reader, AI_STATUS_REG_W);
            ReadBytes(reader, AI_DACRATE_REG_W);
            ReadBytes(reader, AI_BITRATE_REG_W);
            ReadBytes(reader, PI_DRAM_ADDR_REG_RW);
            ReadBytes(reader, PI_CART_ADDR_REG_RW);
            ReadBytes(reader, PI_RD_LEN_REG_RW);
            ReadBytes(reader, PI_WR_LEN_REG_RW);
            ReadBytes(reader, PI_STATUS_REG_R);
            ReadBytes(reader, PI_STATUS_REG_W);
            ReadBytes(reader, PI_BSD_DOM1_LAT_REG_RW);
            ReadBytes(reader, PI_BSD_DOM1_PWD_REG_RW);
            ReadBytes(reader, PI_BSD_DOM1_PGS_REG_RW);
            ReadBytes(reader, PI_BSD_DOM1_RLS_REG_RW);
            ReadBytes(reader, PI_BSD_DOM2_LAT_REG_RW);
            ReadBytes(reader, PI_BSD_DOM2_PWD_REG_RW);
            ReadBytes(reader, PI_BSD_DOM2_PGS_REG_RW);
            ReadBytes(reader, PI_BSD_DOM2_RLS_REG_RW);
            ReadBytes(reader, SI_DRAM_ADDR_REG_RW);
            ReadBytes(reader, SI_PIF_ADDR_RD64B_REG_RW);
            ReadBytes(reader, SI_PIF_ADDR_WR64B_REG_RW);
            ReadBytes(reader, SI_STATUS_REG_R);
            ReadBytes(reader, SI_STATUS_REG_W);
            ReadBytes(reader, RI_MODE_REG_RW);
            ReadBytes(reader, RI_CONFIG_REG_RW);
            ReadBytes(reader, RI_CURRENT_LOAD_REG_RW);
            ReadBytes(reader, RI_SELECT_REG_RW);
            ReadBytes(reader, RI_REFRESH_REG_RW);
            ReadBytes(reader, RI_LATENCY_REG_RW);
            ReadBytes(reader, RI_ERROR_REG_RW);
            ReadBytes(reader, RI_WERROR_REG_RW);
            ReadBytes(reader, RDRAM);
            ReadBytes(reader, RDRAMReg);
            ReadBytes(reader, PIFROM);
            ReadBytes(reader, PIFRAM);
        }

        private static void WriteBytes(BinaryWriter writer, byte[] values)
        {
            writer.Write(values.Length);
            writer.Write(values);
        }

        private static void ReadBytes(BinaryReader reader, byte[] values)
        {
            int length = reader.ReadInt32();
            if (length != values.Length)
                throw new InvalidDataException($"Unsupported N64 byte array length: {length}.");

            int offset = 0;
            while (offset < values.Length)
            {
                int read = reader.Read(values, offset, values.Length - offset);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }
        }

        private static byte[] ReadNewByteArray(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0)
                throw new InvalidDataException($"Unsupported N64 byte array length: {length}.");
            byte[] values = new byte[length];
            int offset = 0;
            while (offset < values.Length)
            {
                int read = reader.Read(values, offset, values.Length - offset);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }
            return values;
        }

        private static void WriteUshortArray(BinaryWriter writer, ushort[] values)
        {
            writer.Write(values.Length);
            for (int i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        private static void ReadUshortArray(BinaryReader reader, ushort[] values)
        {
            int length = reader.ReadInt32();
            if (length != values.Length)
                throw new InvalidDataException($"Unsupported N64 ushort array length: {length}.");
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadUInt16();
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

        private void WriteFramebufferInfos(BinaryWriter writer)
        {
            writer.Write(_fbInfos.Length);
            for (int i = 0; i < _fbInfos.Length; i++)
            {
                TrackedFramebufferInfo info = _fbInfos[i];
                writer.Write(info.Addr);
                writer.Write(info.Size);
                writer.Write(info.Width);
                writer.Write(info.Height);
                writer.Write(info.SetEpoch);
                writer.Write(info.WriteEpoch);
                writer.Write(info.LastReadEpoch);
            }
        }

        private void ReadFramebufferInfos(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length != _fbInfos.Length)
                throw new InvalidDataException($"Unsupported N64 framebuffer info count: {length}.");
            for (int i = 0; i < _fbInfos.Length; i++)
            {
                _fbInfos[i] = new TrackedFramebufferInfo
                {
                    Addr = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    Width = reader.ReadUInt32(),
                    Height = reader.ReadUInt32(),
                    SetEpoch = reader.ReadUInt32(),
                    WriteEpoch = reader.ReadUInt32(),
                    LastReadEpoch = reader.ReadUInt32()
                };
            }
        }

        private static void WriteSpDmaRequest(BinaryWriter writer, SpDmaRequest request)
        {
            writer.Write(request.IsReadFromDram);
            writer.Write(request.MemAddr);
            writer.Write(request.DramAddr);
            writer.Write(request.LengthReg);
        }

        private static SpDmaRequest ReadSpDmaRequest(BinaryReader reader)
        {
            return new SpDmaRequest
            {
                IsReadFromDram = reader.ReadBoolean(),
                MemAddr = reader.ReadUInt32(),
                DramAddr = reader.ReadUInt32(),
                LengthReg = reader.ReadUInt32()
            };
        }

        private static void WriteRspTask(BinaryWriter writer, RspTask task)
        {
            writer.Write(task.Type);
            writer.Write(task.Flags);
            writer.Write(task.Ucode);
            writer.Write(task.UcodeSize);
            writer.Write(task.UcodeData);
            writer.Write(task.UcodeDataSize);
            writer.Write(task.DataPtr);
            writer.Write(task.DataSize);
            writer.Write(task.YieldDataPtr);
            writer.Write(task.YieldDataSize);
        }

        private static RspTask ReadRspTask(BinaryReader reader)
        {
            return new RspTask
            {
                Type = reader.ReadUInt32(),
                Flags = reader.ReadUInt32(),
                Ucode = reader.ReadUInt32(),
                UcodeSize = reader.ReadUInt32(),
                UcodeData = reader.ReadUInt32(),
                UcodeDataSize = reader.ReadUInt32(),
                DataPtr = reader.ReadUInt32(),
                DataSize = reader.ReadUInt32(),
                YieldDataPtr = reader.ReadUInt32(),
                YieldDataSize = reader.ReadUInt32()
            };
        }

        private static void WriteRdpCombine(BinaryWriter writer, RdpCombineMode combine)
        {
            writer.Write(combine.SubARgb0);
            writer.Write(combine.SubBRgb0);
            writer.Write(combine.MulRgb0);
            writer.Write(combine.AddRgb0);
            writer.Write(combine.SubAA0);
            writer.Write(combine.SubBA0);
            writer.Write(combine.MulA0);
            writer.Write(combine.AddA0);
            writer.Write(combine.SubARgb1);
            writer.Write(combine.SubBRgb1);
            writer.Write(combine.MulRgb1);
            writer.Write(combine.AddRgb1);
            writer.Write(combine.SubAA1);
            writer.Write(combine.SubBA1);
            writer.Write(combine.MulA1);
            writer.Write(combine.AddA1);
        }

        private static RdpCombineMode ReadRdpCombine(BinaryReader reader)
        {
            return new RdpCombineMode
            {
                SubARgb0 = reader.ReadInt32(),
                SubBRgb0 = reader.ReadInt32(),
                MulRgb0 = reader.ReadInt32(),
                AddRgb0 = reader.ReadInt32(),
                SubAA0 = reader.ReadInt32(),
                SubBA0 = reader.ReadInt32(),
                MulA0 = reader.ReadInt32(),
                AddA0 = reader.ReadInt32(),
                SubARgb1 = reader.ReadInt32(),
                SubBRgb1 = reader.ReadInt32(),
                MulRgb1 = reader.ReadInt32(),
                AddRgb1 = reader.ReadInt32(),
                SubAA1 = reader.ReadInt32(),
                SubBA1 = reader.ReadInt32(),
                MulA1 = reader.ReadInt32(),
                AddA1 = reader.ReadInt32()
            };
        }

        private void WriteRdpTiles(BinaryWriter writer)
        {
            writer.Write(_rdpTiles.Length);
            for (int i = 0; i < _rdpTiles.Length; i++)
            {
                RdpTileState tile = _rdpTiles[i];
                writer.Write(tile.Format);
                writer.Write(tile.Size);
                writer.Write(tile.Line);
                writer.Write(tile.Tmem);
                writer.Write(tile.Palette);
                writer.Write(tile.MaskS);
                writer.Write(tile.MaskT);
                writer.Write(tile.ShiftS);
                writer.Write(tile.ShiftT);
                writer.Write(tile.ClampS);
                writer.Write(tile.ClampT);
                writer.Write(tile.MirrorS);
                writer.Write(tile.MirrorT);
                writer.Write(tile.Uls);
                writer.Write(tile.Ult);
                writer.Write(tile.Lrs);
                writer.Write(tile.Lrt);
                writer.Write(tile.TileSizeSet);
            }
        }

        private void ReadRdpTiles(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length != _rdpTiles.Length)
                throw new InvalidDataException($"Unsupported N64 RDP tile count: {length}.");
            for (int i = 0; i < _rdpTiles.Length; i++)
            {
                _rdpTiles[i] = new RdpTileState
                {
                    Format = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    Line = reader.ReadUInt32(),
                    Tmem = reader.ReadUInt32(),
                    Palette = reader.ReadUInt32(),
                    MaskS = reader.ReadUInt32(),
                    MaskT = reader.ReadUInt32(),
                    ShiftS = reader.ReadUInt32(),
                    ShiftT = reader.ReadUInt32(),
                    ClampS = reader.ReadBoolean(),
                    ClampT = reader.ReadBoolean(),
                    MirrorS = reader.ReadBoolean(),
                    MirrorT = reader.ReadBoolean(),
                    Uls = reader.ReadUInt32(),
                    Ult = reader.ReadUInt32(),
                    Lrs = reader.ReadUInt32(),
                    Lrt = reader.ReadUInt32(),
                    TileSizeSet = reader.ReadBoolean()
                };
            }
        }

        private static double TicksToMs(long ticks)
        {
            return ticks <= 0 ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static double AvgTicksMs(long ticks, long calls)
        {
            return calls <= 0 ? 0.0 : TicksToMs(ticks) / calls;
        }

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

            PostFramebufferWrite(baseAddress, size, epoch);
        }

        private static uint GetFramebufferBufferSize(TrackedFramebufferInfo info)
        {
            if (info.Addr == 0 || info.Size == 0 || info.Width == 0 || info.Height == 0)
                return 0;

            ulong bufferSize = (ulong)info.Size * info.Width * info.Height;
            return bufferSize > uint.MaxValue ? 0u : (uint)bufferSize;
        }

        private uint GetFramebufferHeightHint()
        {
            uint vStart = ReadBigEndianWord(VI_V_START_REG_RW);
            uint start = (vStart >> 16) & 0x03FFu;
            uint end = vStart & 0x03FFu;
            uint height = (end > start) ? ((end - start) >> 1) : 0u;
            if (height == 0 || height > 480)
                height = 240;
            return height;
        }

        private uint GetFramebufferBytesPerPixelHint()
        {
            uint viStatus = ReadBigEndianWord(VI_STATUS_REG_RW) & 0x3u;
            if (viStatus == 2u)
                return 2u;
            if (viStatus == 3u)
                return 4u;
            return 0u;
        }

        private void RegisterFramebufferInfoFromViRegisters(uint address)
        {
            uint width = ReadBigEndianWord(VI_WIDTH_REG_RW) & 0x0FFFu;
            if (width == 0)
                width = 320u;

            uint height = GetFramebufferHeightHint();
            uint bytesPerPixel = GetFramebufferBytesPerPixelHint();
            if (bytesPerPixel == 0)
                return;

            RegisterFramebufferInfo(address, bytesPerPixel, width, height);
        }

        private void MarkFramebufferPagesDirty(uint address, uint length)
        {
            if (length == 0)
                return;

            uint begin = address & 0x1FFFFFFFu;
            if (begin >= RDRAM.Length)
                return;

            uint end = begin + length - 1u;
            if (end >= RDRAM.Length)
                end = (uint)RDRAM.Length - 1u;

            int startPage = (int)(begin / RdramPageSize);
            int endPage = (int)(end / RdramPageSize);
            for (int page = startPage; page <= endPage; page++)
                _fbDirtyPage[page] = 1;
        }

        private void RegisterFramebufferInfo(uint address, uint bytesPerPixel, uint width, uint height)
        {
            address &= 0x00FFFFFFu;
            if (address < PlausibleFramebufferOriginFloor
                || address >= RDRAM.Length
                || bytesPerPixel == 0
                || width == 0
                || height == 0)
                return;

            ulong requested = (ulong)bytesPerPixel * width * height;
            if (requested == 0)
                return;

            if (address + requested > (ulong)RDRAM.Length)
            {
                ulong remaining = (ulong)RDRAM.Length - address;
                ulong rowSize = (ulong)bytesPerPixel * width;
                if (rowSize == 0)
                    return;

                height = (uint)Math.Max(1UL, remaining / rowSize);
                requested = (ulong)bytesPerPixel * width * height;
                if (requested == 0 || address + requested > (ulong)RDRAM.Length)
                    return;
            }

            uint epoch = ++_fbInfoEpoch;
            int slot = -1;
            uint oldestEpoch = uint.MaxValue;
            int oldestSlot = 0;

            for (int i = 0; i < _fbInfos.Length; i++)
            {
                ref TrackedFramebufferInfo info = ref _fbInfos[i];
                if (info.Addr == address && info.Size == bytesPerPixel && info.Width == width)
                {
                    slot = i;
                    break;
                }

                if (info.Addr == 0 && slot < 0)
                    slot = i;

                if (info.SetEpoch < oldestEpoch)
                {
                    oldestEpoch = info.SetEpoch;
                    oldestSlot = i;
                }
            }

            if (slot < 0)
                slot = oldestSlot;

            _fbInfos[slot] = new TrackedFramebufferInfo
            {
                Addr = address,
                Size = bytesPerPixel,
                Width = width,
                Height = height,
                SetEpoch = epoch,
                WriteEpoch = epoch,
                LastReadEpoch = 0
            };

            MarkFramebufferPagesDirty(address, (uint)requested);

            if (TraceFramebufferLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64FB] register slot={slot} addr=0x{address:x8} size={bytesPerPixel} width={width} height={height} " +
                    $"setEpoch={epoch} pc=0x{Registers.R4300.PC:x8}");
            }
        }

        private void TrackFramebufferInfosFromDpcBuffer(uint start, uint end)
        {
            uint baseAddress = start & 0x00FFFFF8u;
            uint endAddress = end & 0x00FFFFF8u;
            if (baseAddress >= RDRAM.Length || endAddress > RDRAM.Length || endAddress <= baseAddress)
                return;

            uint length = endAddress - baseAddress;
            if (length < 8)
                return;

            if (length > 0x20000u)
                length = 0x20000u;

            uint heightHint = GetFramebufferHeightHint();
            for (uint offset = 0; offset + 8u <= length; offset += 8u)
            {
                uint word0 = ReadUInt32Physical(baseAddress + offset);
                if ((word0 >> 24) != 0xFFu)
                    continue;

                uint bytesPerPixel;
                switch ((word0 >> 19) & 0x3u)
                {
                    case 0u:
                    case 1u:
                        bytesPerPixel = 1u;
                        break;
                    case 2u:
                        bytesPerPixel = 2u;
                        break;
                    case 3u:
                        bytesPerPixel = 4u;
                        break;
                    default:
                        bytesPerPixel = 0u;
                        break;
                }

                uint width = (word0 & 0x0FFFu) + 1u;
                uint address = ReadUInt32Physical(baseAddress + offset + 4u) & 0x00FFFFFFu;
                if (TraceFramebufferLifecycle)
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64FB] dpc-setcolor start=0x{baseAddress:x8} end=0x{endAddress:x8} off=0x{offset:x} " +
                        $"w0=0x{word0:x8} addr=0x{address:x8} size={bytesPerPixel} width={width} heightHint={heightHint}");
                }
                RegisterFramebufferInfo(address, bytesPerPixel, width, heightHint);
            }
        }

        private uint ExecuteRdpDisplayList(uint start, uint end)
        {
            uint current = start & 0x00FFFFF8u;
            uint endAddress = end & 0x00FFFFF8u;
            if (endAddress <= current)
                return current;

            long perfStart = StartPerfTimer();
            uint maxEnd = current + Math.Min(endAddress - current, 0x20000u);
            bool xbusDmem = (ReadBigEndianWord(DPC_STATUS_REG_R) & DpcStatusXbusDmemDma) != 0;
            int[] commandCounts = TraceRdpCommands && _traceRdpSummaryCount < TraceRdpSummaryLimit ? new int[64] : null;
            uint firstUnhandledAddress = 0;
            uint firstUnhandledW0 = 0;
            uint firstUnhandledW1 = 0;
            int firstUnhandledCommand = -1;
            int commandCount = 0;
            int handledCount = 0;
            while (current + 8u <= maxEnd)
            {
                uint w0 = ReadRdpCommandWord(current, xbusDmem);
                uint w1 = ReadRdpCommandWord(current + 4u, xbusDmem);
                int command = (int)((w0 >> 24) & 0x3Fu);
                int words = GetRdpCommandWordLength(command);
                if (words < 2 || current + (uint)(words * 4) > maxEnd)
                    break;

                commandCount++;
                if (commandCounts != null)
                    commandCounts[command]++;

                bool handled = true;
                switch (command)
                {
                    case 0x08: // Triangle
                    case 0x09: // TriangleZ
                    case 0x0A: // TriangleTexture
                    case 0x0B: // TriangleTextureZ
                    case 0x0C: // TriangleShade
                    case 0x0D: // TriangleShadeZ
                    case 0x0E: // TriangleShadeTexture
                    case 0x0F: // TriangleShadeTextureZ
                        Interlocked.Increment(ref _rdpTriangleCommandCount);
                        ExecuteRdpTriangle(command, current, xbusDmem);
                        break;
                    case 0x24: // TextureRectangle
                    case 0x25: // TextureRectangleFlip
                        Interlocked.Increment(ref _rdpTextureRectangleCommandCount);
                        ExecuteRdpTextureRectangle(command, w0, w1, ReadRdpCommandWord(current + 8u, xbusDmem), ReadRdpCommandWord(current + 12u, xbusDmem));
                        break;
                    case 0x26: // SyncLoad
                    case 0x27: // SyncPipe
                    case 0x28: // SyncTile
                    case 0x29: // SyncFull
                        break;
                    case 0x2D: // SetScissor
                        ExecuteRdpSetScissor(w0, w1);
                        break;
                    case 0x2E: // SetPrimDepth
                        _rdpPrimitiveDepth = (w1 >> 16) & 0x7FFFu;
                        _rdpPrimitiveDeltaZ = w1 & 0xFFFFu;
                        break;
                    case 0x2F: // SetOtherModes
                        ExecuteRdpSetOtherModes(w0, w1);
                        break;
                    case 0x3C: // SetCombineMode
                        ExecuteRdpSetCombine(w0, w1);
                        break;
                    case 0x30: // LoadTLut
                        ExecuteRdpLoadTlut(w0, w1);
                        break;
                    case 0x32: // SetTileSize
                        ExecuteRdpSetTileSize(w0, w1);
                        break;
                    case 0x33: // LoadBlock
                        ExecuteRdpLoadBlock(w0, w1);
                        break;
                    case 0x34: // LoadTile
                        ExecuteRdpLoadTile(w0, w1);
                        break;
                    case 0x35: // SetTile
                        ExecuteRdpSetTile(w0, w1);
                        break;
                    case 0x36: // FillRectangle
                        Interlocked.Increment(ref _rdpFillRectangleCommandCount);
                        ExecuteRdpFillRectangle(w0, w1);
                        break;
                    case 0x37: // SetFillColor
                        _rdpFillColor = w1;
                        break;
                    case 0x38: // SetFogColor
                        _rdpFogColor = w1;
                        break;
                    case 0x39: // SetBlendColor
                        _rdpBlendColor = w1;
                        break;
                    case 0x3A: // SetPrimColor
                        _rdpPrimColor = w1;
                        break;
                    case 0x3B: // SetEnvColor
                        _rdpEnvColor = w1;
                        break;
                    case 0x3D: // SetTextureImage
                        _rdpTextureImageFormat = (w0 >> 21) & 0x7u;
                        _rdpTextureImageSize = (w0 >> 19) & 0x3u;
                        _rdpTextureImageWidth = (w0 & 0x03FFu) + 1u;
                        _rdpTextureImageAddress = w1 & 0x00FFFFFFu;
                        break;
                    case 0x3E: // SetMaskImage
                        _rdpMaskImageAddress = w1 & 0x00FFFFFFu;
                        break;
                    case 0x3F: // SetColorImage
                        Interlocked.Increment(ref _rdpSetColorImageCommandCount);
                        _rdpColorImageSize = (w0 >> 19) & 0x3u;
                        _rdpColorImageWidth = (w0 & 0x03FFu) + 1u;
                        _rdpColorImageAddress = w1 & 0x00FFFFFFu;
                        RegisterFramebufferInfo(_rdpColorImageAddress, RdpBytesPerPixel(_rdpColorImageSize), _rdpColorImageWidth, GetFramebufferHeightHint());
                        break;
                    default:
                        handled = false;
                        if (firstUnhandledCommand < 0)
                        {
                            firstUnhandledCommand = command;
                            firstUnhandledAddress = current;
                            firstUnhandledW0 = w0;
                            firstUnhandledW1 = w1;
                        }
                        break;
                }

                if (handled)
                    handledCount++;

                current += (uint)(words * 4);
            }

            if (commandCount > 0)
            {
                Interlocked.Increment(ref _rdpDisplayListCount);
                Interlocked.Add(ref _rdpCommandCount, commandCount);
                Interlocked.Add(ref _rdpHandledCommandCount, handledCount);
            }

            if (commandCounts != null && commandCount > 0)
            {
                string summary = "";
                for (int i = 0; i < commandCounts.Length; i++)
                {
                    if (commandCounts[i] == 0)
                        continue;

                    if (summary.Length > 0)
                        summary += " ";
                    summary += $"{i:x2}:{commandCounts[i]}";
                }

                Common.Logger.PrintWarningLine(
                    $"[N64RDP] list start=0x{start:x8} end=0x{end:x8} xbus={xbusDmem} cmds={commandCount} handled={handledCount} " +
                    $"hist={summary} firstUnhandled=0x{firstUnhandledCommand:x2}@0x{firstUnhandledAddress:x8} w0=0x{firstUnhandledW0:x8} w1=0x{firstUnhandledW1:x8}");
                _traceRdpSummaryCount++;
            }

            AddPerfTicks(ref _perfRdpDisplayListTicks, ref _perfRdpDisplayListCalls, perfStart);
            return current;
        }

        private uint ReadRdpCommandWord(uint address, bool xbusDmem)
        {
            if (xbusDmem)
                return ReadSpDmemWord(address & 0x0FFFu);

            uint physical = address & 0x00FFFFFCu;
            if (physical + 4u > RDRAM.Length)
                return 0;
            return ReadUInt32Physical(physical);
        }

        private ulong ReadRdpCommandDoubleWord(uint address, bool xbusDmem)
        {
            return ((ulong)ReadRdpCommandWord(address, xbusDmem) << 32) |
                   ReadRdpCommandWord(address + 4u, xbusDmem);
        }

        private static int GetRdpCommandWordLength(int command)
        {
            if (command >= 0x08 && command <= 0x0F)
            {
                bool shaded = (command & 0x04) != 0;
                bool textured = (command & 0x02) != 0;
                bool zBuffered = (command & 0x01) != 0;
                int words = 8;
                if (shaded)
                    words += 16;
                if (textured)
                    words += 16;
                if (zBuffered)
                    words += 4;
                return words;
            }

            return command == 0x24 || command == 0x25 ? 4 : 2;
        }

        private void ExecuteRdpTriangle(int command, uint commandAddress, bool xbusDmem)
        {
            uint bytesPerPixel = RdpBytesPerPixel(_rdpColorImageSize);
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0)
                return;

            uint w0 = ReadRdpCommandWord(commandAddress, xbusDmem);
            uint w1 = ReadRdpCommandWord(commandAddress + 4u, xbusDmem);
            uint w2 = ReadRdpCommandWord(commandAddress + 8u, xbusDmem);
            uint w3 = ReadRdpCommandWord(commandAddress + 12u, xbusDmem);
            uint w4 = ReadRdpCommandWord(commandAddress + 16u, xbusDmem);
            uint w5 = ReadRdpCommandWord(commandAddress + 20u, xbusDmem);
            uint w6 = ReadRdpCommandWord(commandAddress + 24u, xbusDmem);
            uint w7 = ReadRdpCommandWord(commandAddress + 28u, xbusDmem);

            double yl = RdpTriangleYToScreen(w0 & 0x3FFFu);
            double ym = RdpTriangleYToScreen((w1 >> 16) & 0x3FFFu);
            double yh = RdpTriangleYToScreen(w1 & 0x3FFFu);
            double xl = RdpTriangleXToScreen(w2);
            double dxldy = RdpTriangleDeltaXToScreen(w3);
            double xh = RdpTriangleXToScreen(w4);
            double dxhdy = RdpTriangleDeltaXToScreen(w5);
            double xm = RdpTriangleXToScreen(w6);
            double dxmdy = RdpTriangleDeltaXToScreen(w7);
            bool flip = (w0 & 0x00800000u) != 0;
            int tileIndex = (int)((w0 >> 16) & 0x7u);

            uint rgba = SelectRdpTriangleColor(command, commandAddress, xbusDmem);
            RdpTriangleDepthCoefficients triangleDepth = default;
            bool useDepth = ShouldUseRdpDepth(command)
                && TryReadRdpDepthCoefficients(command, commandAddress, xbusDmem, out triangleDepth);
            bool wrote = false;
            if ((command & 0x02) != 0)
            {
                RdpTriangleShadeCoefficients textureShade = default;
                bool hasShade = (command & 0x04) != 0
                    && TryReadRdpShadeCoefficients(commandAddress, xbusDmem, out textureShade);
                wrote = DrawRdpTexturedTriangle(
                    command,
                    commandAddress,
                    xbusDmem,
                    hasShade ? textureShade : default,
                    hasShade,
                    useDepth ? triangleDepth : default,
                    useDepth,
                    tileIndex,
                    xh,
                    dxhdy,
                    xm,
                    dxmdy,
                    xl,
                    dxldy,
                    yh,
                    ym,
                    yl,
                    flip,
                    bytesPerPixel);
            }
            if (!wrote)
            {
                if ((command & 0x04) != 0 && TryReadRdpShadeCoefficients(commandAddress, xbusDmem, out RdpTriangleShadeCoefficients shade))
                    wrote = DrawRdpShadedTriangle(xh, dxhdy, xm, dxmdy, xl, dxldy, yh, ym, yl, flip, shade, useDepth ? triangleDepth : default, useDepth, bytesPerPixel);
                else
                    wrote = DrawRdpTriangle(xh, dxhdy, xm, dxmdy, xl, dxldy, yh, ym, yl, flip, rgba, useDepth ? triangleDepth : default, useDepth, bytesPerPixel);
            }

            if (TraceRdpCommands && _traceRdpTriangleCount < TraceRdpTriangleLimit)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64RDP] triangle cmd=0x{command:x2} ci=0x{_rdpColorImageAddress:x8} size={_rdpColorImageSize} width={_rdpColorImageWidth} " +
                    $"yh={yh:0.##} ym={ym:0.##} yl={yl:0.##} xh={xh:0.##}/{dxhdy:0.####} xm={xm:0.##}/{dxmdy:0.####} xl={xl:0.##}/{dxldy:0.####} flip={flip} tile={tileIndex} " +
                    $"rgba=0x{rgba:x8} wrote={wrote}");
                _traceRdpTriangleCount++;
            }
        }

        private bool DrawRdpTexturedTriangle(
            int command,
            uint commandAddress,
            bool xbusDmem,
            RdpTriangleShadeCoefficients shade,
            bool modulateShade,
            RdpTriangleDepthCoefficients depth,
            bool useDepth,
            int tileIndex,
            double xh,
            double dxhdy,
            double xm,
            double dxmdy,
            double xl,
            double dxldy,
            double yh,
            double ym,
            double yl,
            bool flip,
            uint bytesPerPixel)
        {
            long perfStart = StartPerfTimer();
            try
            {
            if ((uint)tileIndex >= _rdpTiles.Length || yl <= yh)
                return false;

            RdpTileState tile = _rdpTiles[tileIndex];
            if (!TryReadRdpTextureCoefficients(command, commandAddress, xbusDmem, out RdpTriangleTextureCoefficients tex))
                return false;

            uint maxRows = (uint)((RDRAM.Length - _rdpColorImageAddress) / (_rdpColorImageWidth * bytesPerPixel));
            if (maxRows == 0)
                return false;

            int firstY = Math.Max(Math.Max(0, _rdpScissorY0), (int)Math.Floor(yh));
            int lastY = Math.Min(Math.Min((int)maxRows - 1, _rdpScissorY1), (int)Math.Ceiling(yl) - 1);
            if (lastY < firstY)
                return false;

            bool wroteAny = false;
            long sampleHits = 0;
            long nonZeroSampleHits = 0;
            long sampleMisses = 0;
            long zeroSampleHits = 0;
            long depthRejects = 0;
            int firstSampleS = 0;
            int firstSampleT = 0;
            uint firstSampleRgba = 0;
            bool hasFirstSample = false;
            for (int y = firstY; y <= lastY; y++)
            {
                double sampleY = y + 0.5;
                if (sampleY < yh || sampleY >= yl)
                    continue;

                double majorX = xh + (sampleY - yh) * dxhdy;
                double minorX = sampleY < ym
                    ? xm + (sampleY - yh) * dxmdy
                    : xl + (sampleY - ym) * dxldy;
                double left = flip ? majorX : minorX;
                double right = flip ? minorX : majorX;
                if (right < left)
                {
                    double tmp = left;
                    left = right;
                    right = tmp;
                }

                int firstX = Math.Max(Math.Max(0, _rdpScissorX0), (int)Math.Floor(left));
                int lastX = Math.Min(Math.Min((int)_rdpColorImageWidth - 1, _rdpScissorX1), (int)Math.Ceiling(right) - 1);
                if (lastX < firstX)
                    continue;

                long yStep = (long)Math.Round(sampleY - yh);
                long rowS = tex.S + yStep * tex.DsDe;
                long rowT = tex.T + yStep * tex.DtDe;
                long rowW = tex.W + yStep * tex.DwDe;
                long rowZ = useDepth ? RdpDepthRowStart(depth, yStep) : 0;
                long rowR = modulateShade ? shade.R + yStep * (long)shade.DrDe : 0;
                long rowG = modulateShade ? shade.G + yStep * (long)shade.DgDe : 0;
                long rowB = modulateShade ? shade.B + yStep * (long)shade.DbDe : 0;
                long rowA = modulateShade ? shade.A + yStep * (long)shade.DaDe : 0;
                double spanAnchorX = flip ? left : right;
                uint rowStart = _rdpColorImageAddress + (((uint)y * _rdpColorImageWidth + (uint)firstX) * bytesPerPixel);
                bool rowWrote = false;
                for (int x = firstX; x <= lastX; x++)
                {
                    long xStep = (long)Math.Round(x + 0.5 - spanAnchorX);
                    RdpTriangleTextureFixedToTexels(
                        rowS + xStep * tex.DsDx,
                        rowT + xStep * tex.DtDx,
                        rowW + xStep * tex.DwDx,
                        EnableRdpPerspectiveTexture && _rdpOtherModesPerspectiveTexture,
                        out int sampleS,
                        out int sampleT);
                    if (!SampleRdpTexture(tile, sampleS, sampleT, out uint rgba))
                    {
                        sampleMisses++;
                        continue;
                    }

                    if (modulateShade)
                    {
                        uint shadeRgba = RdpShadeToRgba(
                            rowR + xStep * (long)shade.DrDx,
                            rowG + xStep * (long)shade.DgDx,
                            rowB + xStep * (long)shade.DbDx,
                            rowA + xStep * (long)shade.DaDx);
                        rgba = _rdpCombineModeSet
                            ? ApplyRdpColorCombiner(rgba, shadeRgba)
                            : ModulateRdpRgba(rgba, shadeRgba);
                    }
                    else if (_rdpCombineModeSet)
                    {
                        rgba = ApplyRdpColorCombiner(rgba, 0xFFFFFFFFu);
                    }

                    sampleHits++;
                    if (ShouldRejectRdpAlpha(rgba))
                    {
                        zeroSampleHits++;
                        continue;
                    }

                    if (!hasFirstSample)
                    {
                        firstSampleS = sampleS;
                        firstSampleT = sampleT;
                        firstSampleRgba = rgba;
                        hasFirstSample = true;
                    }

                    if (!IsRgbaNonZero(rgba))
                    {
                        zeroSampleHits++;
                        continue;
                    }

                    uint address = _rdpColorImageAddress + (((uint)y * _rdpColorImageWidth + (uint)x) * bytesPerPixel);
                    if (useDepth && !PassRdpDepthTest((uint)y * _rdpColorImageWidth + (uint)x, rowZ + xStep * (long)depth.DzDx, depth.DzPix))
                    {
                        depthRejects++;
                        continue;
                    }

                    WriteRdpRgbaPixel(address, rgba, bytesPerPixel);
                    nonZeroSampleHits++;
                    rowWrote = true;
                    wroteAny = true;
                }

                if (rowWrote)
                    NoteRdramWriteRange(rowStart, (uint)(lastX - firstX + 1) * bytesPerPixel);
            }

            if (TraceRdpCommands && _traceRdpTriangleWriteCount < TraceRdpTriangleWriteLimit)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64RDP] tritex-write ci=0x{_rdpColorImageAddress:x8} tile={tileIndex} hits={sampleHits} nz={nonZeroSampleHits} zero={zeroSampleHits} zrej={depthRejects} miss={sampleMisses} " +
                    $"firstS={firstSampleS} firstT={firstSampleT} firstRgba=0x{firstSampleRgba:x8} " +
                    $"tex=s{tex.S}:t{tex.T}:w{tex.W}:dsdx{tex.DsDx}:dtdx{tex.DtDx}:dwdx{tex.DwDx}:dsde{tex.DsDe}:dtde{tex.DtDe}:dwde{tex.DwDe}:dsdy{tex.DsDy}:dtdy{tex.DtDy}:dwdy{tex.DwDy} " +
                    $"raw={tex.Raw0:x16},{tex.Raw1:x16},{tex.Raw2:x16},{tex.Raw3:x16},{tex.Raw4:x16},{tex.Raw5:x16},{tex.Raw6:x16},{tex.Raw7:x16} " +
                    $"tile=fmt{tile.Format}:sz{tile.Size}:line{tile.Line}:tmem0x{tile.Tmem:x}:mask{tile.MaskS}/{tile.MaskT}:shift{tile.ShiftS}/{tile.ShiftT}:clamp{tile.ClampS}/{tile.ClampT}:mirror{tile.MirrorS}/{tile.MirrorT}:uls{tile.Uls}:ult{tile.Ult}:lrs{tile.Lrs}:lrt{tile.Lrt}");
                _traceRdpTriangleWriteCount++;
            }

            NoteRdpPixelWrites(sampleHits, nonZeroSampleHits);
            if (wroteAny)
            {
                MarkRdpColorImageWritten(bytesPerPixel);
                if (nonZeroSampleHits >= 256)
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, nonZeroSampleHits);
                else if (HasRdpVisibleFramebufferContent(bytesPerPixel, 512u))
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, 512);
            }
            return wroteAny;
            }
            finally
            {
                AddPerfTicks(ref _perfRdpTexturedTriangleTicks, ref _perfRdpTexturedTriangleCalls, perfStart);
            }
        }

        private struct RdpTriangleTextureCoefficients
        {
            public int S;
            public int T;
            public int W;
            public int DsDx;
            public int DtDx;
            public int DwDx;
            public int DsDe;
            public int DtDe;
            public int DwDe;
            public int DsDy;
            public int DtDy;
            public int DwDy;
            public ulong Raw0;
            public ulong Raw1;
            public ulong Raw2;
            public ulong Raw3;
            public ulong Raw4;
            public ulong Raw5;
            public ulong Raw6;
            public ulong Raw7;
        }

        private struct RdpTriangleDepthCoefficients
        {
            public int Z;
            public int DzDx;
            public int DzDe;
            public int DzDy;
            public uint DzPix;
        }

        private bool TryReadRdpTextureCoefficients(int command, uint commandAddress, bool xbusDmem, out RdpTriangleTextureCoefficients coefficients)
        {
            coefficients = default;
            if ((command & 0x02) == 0)
                return false;

            int textureBase = 4;
            if ((command & 0x04) != 0)
                textureBase += 8;
            uint textureAddress = commandAddress + (uint)(textureBase * 8);

            ulong texture0 = ReadRdpCommandDoubleWord(textureAddress, xbusDmem);
            ulong texture1 = ReadRdpCommandDoubleWord(textureAddress + 8u, xbusDmem);
            ulong texture2 = ReadRdpCommandDoubleWord(textureAddress + 16u, xbusDmem);
            ulong texture3 = ReadRdpCommandDoubleWord(textureAddress + 24u, xbusDmem);
            ulong texture4 = ReadRdpCommandDoubleWord(textureAddress + 32u, xbusDmem);
            ulong texture5 = ReadRdpCommandDoubleWord(textureAddress + 40u, xbusDmem);
            ulong texture6 = ReadRdpCommandDoubleWord(textureAddress + 48u, xbusDmem);
            ulong texture7 = ReadRdpCommandDoubleWord(textureAddress + 56u, xbusDmem);

            coefficients.S = RdpPackedTextureComponent(texture0, texture2, 32, 48);
            coefficients.T = RdpPackedTextureComponent(texture0, texture2, 16, 32);
            coefficients.W = RdpPackedTextureComponent(texture0, texture2, 0, 16);
            coefficients.DsDx = RdpPackedTextureComponent(texture1, texture3, 32, 48);
            coefficients.DtDx = RdpPackedTextureComponent(texture1, texture3, 16, 32);
            coefficients.DwDx = RdpPackedTextureComponent(texture1, texture3, 0, 16);
            coefficients.DsDe = RdpPackedTextureComponent(texture4, texture6, 32, 48);
            coefficients.DtDe = RdpPackedTextureComponent(texture4, texture6, 16, 32);
            coefficients.DwDe = RdpPackedTextureComponent(texture4, texture6, 0, 16);
            coefficients.DsDy = RdpPackedTextureComponent(texture5, texture7, 32, 48);
            coefficients.DtDy = RdpPackedTextureComponent(texture5, texture7, 16, 32);
            coefficients.DwDy = RdpPackedTextureComponent(texture5, texture7, 0, 16);
            coefficients.Raw0 = texture0;
            coefficients.Raw1 = texture1;
            coefficients.Raw2 = texture2;
            coefficients.Raw3 = texture3;
            coefficients.Raw4 = texture4;
            coefficients.Raw5 = texture5;
            coefficients.Raw6 = texture6;
            coefficients.Raw7 = texture7;
            return true;
        }

        private bool TryReadRdpDepthCoefficients(int command, uint commandAddress, bool xbusDmem, out RdpTriangleDepthCoefficients coefficients)
        {
            coefficients = default;
            if ((command & 0x01) == 0)
                return false;

            int depthBase = 4;
            if ((command & 0x04) != 0)
                depthBase += 8;
            if ((command & 0x02) != 0)
                depthBase += 8;

            ulong depth0 = ReadRdpCommandDoubleWord(commandAddress + (uint)(depthBase * 8), xbusDmem);
            ulong depth1 = ReadRdpCommandDoubleWord(commandAddress + (uint)((depthBase + 1) * 8), xbusDmem);
            int dzDx = unchecked((int)depth0);
            int dzDy = unchecked((int)depth1);
            uint dzPix = NormalizeRdpDzPix(
                (uint)((dzDx >> 16) & 0xFFFF),
                (uint)((dzDy >> 16) & 0xFFFF));

            coefficients = new RdpTriangleDepthCoefficients
            {
                Z = unchecked((int)(depth0 >> 32)),
                DzDx = dzDx,
                DzDe = unchecked((int)(depth1 >> 32)),
                DzDy = dzDy,
                DzPix = dzPix
            };
            return true;
        }

        private bool ShouldUseRdpDepth(int command)
        {
            return EnableRdpDepth
                && (command & 0x01) != 0
                && _rdpMaskImageAddress >= PlausibleFramebufferOriginFloor
                && (_rdpOtherModesZCompare || _rdpOtherModesZUpdate);
        }

        private long RdpDepthRowStart(RdpTriangleDepthCoefficients depth, long yStep)
        {
            if (_rdpOtherModesZSourceSel)
                return (long)_rdpPrimitiveDepth << 16;

            return depth.Z + yStep * (long)depth.DzDe;
        }

        private bool PassRdpDepthTest(uint pixelIndex, long zFixed, uint dzPix)
        {
            uint sz = RdpDepthFixedToComparator(zFixed);
            uint zIndex = (_rdpMaskImageAddress >> 1) + pixelIndex;
            uint zAddress = zIndex << 1;
            if (zAddress + 1u >= RDRAM.Length || zIndex >= _rdpHiddenBits.Length)
                return true;

            ushort zVal = (ushort)((RDRAM[zAddress] << 8) | RDRAM[zAddress + 1u]);
            uint rawDzMem = (uint)(((zVal & 3) << 2) | (_rdpHiddenBits[zIndex] & 3));
            uint oldZ = RdpZDecompressTable[(zVal >> 2) & 0x3FFFu];
            uint dzMem = 1u << (int)Math.Min(rawDzMem, 31u);
            if (dzMem == 0x8000u)
                dzMem = 0xFFFFu;
            else
            {
                int precisionFactor = (zVal >> 13) & 0xF;
                if (precisionFactor < 3)
                {
                    uint modifier = 16u >> precisionFactor;
                    dzMem <<= 1;
                    if (dzMem < modifier)
                        dzMem = modifier;
                }
            }

            uint dzNew = Math.Max(dzMem, dzPix == 0 ? 1u : dzPix) << 3;
            bool inFront = sz < oldZ;
            bool farther = sz + dzNew >= oldZ;
            int diff = unchecked((int)sz - (int)dzNew);
            bool nearer = diff <= (int)oldZ;
            bool max = oldZ == 0x3FFFFu || zVal == 0u;

            bool pass;
            if (!_rdpOtherModesZCompare)
                pass = true;
            else
            {
                switch (_rdpOtherModesZMode & 0x3u)
                {
                    case 2u:
                        pass = inFront || max;
                        break;
                    case 3u:
                        pass = farther && nearer && !max;
                        break;
                    default:
                        pass = max || nearer;
                        break;
                }
            }

            if (pass && _rdpOtherModesZUpdate)
                StoreRdpDepth(zIndex, sz, CompressRdpDz(dzPix == 0 ? 1u : dzPix));

            return pass;
        }

        private void StoreRdpDepth(uint zIndex, uint z, uint dzPixEncoded)
        {
            uint zAddress = zIndex << 1;
            if (zAddress + 1u >= RDRAM.Length || zIndex >= _rdpHiddenBits.Length)
                return;

            ushort zVal = (ushort)(RdpZCompressTable[z & 0x3FFFFu] | (ushort)(dzPixEncoded >> 2));
            RDRAM[zAddress] = (byte)(zVal >> 8);
            RDRAM[zAddress + 1u] = (byte)zVal;
            _rdpHiddenBits[zIndex] = (byte)(dzPixEncoded & 3u);
        }

        private static uint RdpDepthFixedToComparator(long zFixed)
        {
            long sz = (zFixed >> 10) & 0x3FFFFFL;
            if (sz > 0x3FFFFL)
                return 0x3FFFFu;
            return (uint)sz;
        }

        private static uint NormalizeRdpDzPix(uint dzDxHigh, uint dzDyHigh)
        {
            uint sum = AbsRdpDzComponent(dzDxHigh) + AbsRdpDzComponent(dzDyHigh);
            if ((sum & 0xC000u) != 0)
                return 0x8000u;
            if ((sum & 0xFFFFu) == 0)
                return 1u;

            for (uint count = 0x2000u; count > 0; count >>= 1)
            {
                if ((sum & count) != 0)
                    return count << 1;
            }

            return 1u;
        }

        private static uint AbsRdpDzComponent(uint value)
        {
            value &= 0xFFFFu;
            return (value & 0x8000u) != 0 ? (~value) & 0x7FFFu : value;
        }

        private static uint CompressRdpDz(uint value)
        {
            uint j = 0;
            if ((value & 0xFF00u) != 0)
                j |= 8u;
            if ((value & 0xF0F0u) != 0)
                j |= 4u;
            if ((value & 0xCCCCu) != 0)
                j |= 2u;
            if ((value & 0xAAAAu) != 0)
                j |= 1u;
            return j;
        }

        private static int RdpPackedTextureComponent(ulong highWord, ulong lowWord, int highShift, int lowShift)
        {
            return unchecked((int)(((highWord >> highShift) & 0xFFFF0000UL) | ((lowWord >> lowShift) & 0x0000FFFFUL)));
        }

        private static int RdpTriangleTextureFixedToTexel(long value)
        {
            long texel = value >> 19;
            if (texel > int.MaxValue)
                return int.MaxValue;
            if (texel < int.MinValue)
                return int.MinValue;
            return (int)texel;
        }

        private static void RdpTriangleTextureFixedToTexels(
            long sFixed,
            long tFixed,
            long wFixed,
            bool perspective,
            out int s,
            out int t)
        {
            if (!perspective)
            {
                s = RdpTriangleTextureFixedToTexel(sFixed);
                t = RdpTriangleTextureFixedToTexel(tFixed);
                return;
            }

            int swSigned = (short)(wFixed >> 16);
            if (swSigned <= 0)
            {
                s = RdpTriangleTextureFixedToTexel(sFixed);
                t = RdpTriangleTextureFixedToTexel(tFixed);
                return;
            }

            int ss = (short)(sFixed >> 16);
            int st = (short)(tFixed >> 16);
            int table = RdpTextureCoordinateDivideTable[swSigned & 0x7FFF];
            int shift = table & 0xF;
            int reciprocal = table >> 4;
            int sProduct = ss * reciprocal;
            int tProduct = st * reciprocal;
            int sCoordinate = shift == 0xE ? sProduct << 1 : sProduct >> (13 - shift);
            int tCoordinate = shift == 0xE ? tProduct << 1 : tProduct >> (13 - shift);
            s = SignExtend17(sCoordinate & 0x1FFFF) >> 5;
            t = SignExtend17(tCoordinate & 0x1FFFF) >> 5;
        }

        private bool DrawRdpTriangle(
            double xh,
            double dxhdy,
            double xm,
            double dxmdy,
            double xl,
            double dxldy,
            double yh,
            double ym,
            double yl,
            bool flip,
            uint rgba,
            RdpTriangleDepthCoefficients depth,
            bool useDepth,
            uint bytesPerPixel)
        {
            if (yl <= yh)
                return false;

            uint maxRows = (uint)((RDRAM.Length - _rdpColorImageAddress) / (_rdpColorImageWidth * bytesPerPixel));
            if (maxRows == 0)
                return false;

            int firstY = Math.Max(Math.Max(0, _rdpScissorY0), (int)Math.Floor(yh));
            int lastY = Math.Min(Math.Min((int)maxRows - 1, _rdpScissorY1), (int)Math.Ceiling(yl) - 1);
            if (lastY < firstY)
                return false;

            bool wroteAny = false;
            long writtenPixels = 0;
            for (int y = firstY; y <= lastY; y++)
            {
                double sampleY = y + 0.5;
                if (sampleY < yh || sampleY >= yl)
                    continue;

                double majorX = xh + (sampleY - yh) * dxhdy;
                double minorX = sampleY < ym
                    ? xm + (sampleY - yh) * dxmdy
                    : xl + (sampleY - ym) * dxldy;
                double left = flip ? majorX : minorX;
                double right = flip ? minorX : majorX;
                if (right < left)
                {
                    double tmp = left;
                    left = right;
                    right = tmp;
                }

                int firstX = Math.Max(Math.Max(0, _rdpScissorX0), (int)Math.Floor(left));
                int lastX = Math.Min(Math.Min((int)_rdpColorImageWidth - 1, _rdpScissorX1), (int)Math.Ceiling(right) - 1);
                if (lastX < firstX)
                    continue;

                long yStep = useDepth ? (long)Math.Round(sampleY - yh) : 0;
                long rowZ = useDepth ? RdpDepthRowStart(depth, yStep) : 0;
                long rowWrittenPixels = 0;
                for (int x = firstX; x <= lastX; x++)
                {
                    if (useDepth)
                    {
                        long xStep = (long)Math.Round(x + 0.5 - left);
                        if (!PassRdpDepthTest((uint)y * _rdpColorImageWidth + (uint)x, rowZ + xStep * (long)depth.DzDx, depth.DzPix))
                            continue;
                    }

                    uint address = _rdpColorImageAddress + (((uint)y * _rdpColorImageWidth + (uint)x) * bytesPerPixel);
                    WriteRdpRgbaPixel(address, rgba, bytesPerPixel);
                    wroteAny = true;
                    rowWrittenPixels++;
                }
                writtenPixels += rowWrittenPixels;

                if (rowWrittenPixels > 0)
                {
                    uint rowStart = _rdpColorImageAddress + (((uint)y * _rdpColorImageWidth + (uint)firstX) * bytesPerPixel);
                    NoteRdramWriteRange(rowStart, (uint)(lastX - firstX + 1) * bytesPerPixel);
                }
            }

            NoteRdpPixelWrites(writtenPixels, IsRgbaNonZero(rgba) ? writtenPixels : 0);
            if (wroteAny)
            {
                MarkRdpColorImageWritten(bytesPerPixel);
                if (writtenPixels >= 256 && IsRgbaNonZero(rgba))
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, writtenPixels);
            }
            return wroteAny;
        }

        private struct RdpTriangleShadeCoefficients
        {
            public int R;
            public int G;
            public int B;
            public int A;
            public int DrDx;
            public int DgDx;
            public int DbDx;
            public int DaDx;
            public int DrDe;
            public int DgDe;
            public int DbDe;
            public int DaDe;
        }

        private bool DrawRdpShadedTriangle(
            double xh,
            double dxhdy,
            double xm,
            double dxmdy,
            double xl,
            double dxldy,
            double yh,
            double ym,
            double yl,
            bool flip,
            RdpTriangleShadeCoefficients shade,
            RdpTriangleDepthCoefficients depth,
            bool useDepth,
            uint bytesPerPixel)
        {
            if (yl <= yh)
                return false;

            uint maxRows = (uint)((RDRAM.Length - _rdpColorImageAddress) / (_rdpColorImageWidth * bytesPerPixel));
            if (maxRows == 0)
                return false;

            int firstY = Math.Max(Math.Max(0, _rdpScissorY0), (int)Math.Floor(yh));
            int lastY = Math.Min(Math.Min((int)maxRows - 1, _rdpScissorY1), (int)Math.Ceiling(yl) - 1);
            if (lastY < firstY)
                return false;

            bool wroteAny = false;
            long writtenPixels = 0;
            for (int y = firstY; y <= lastY; y++)
            {
                double sampleY = y + 0.5;
                if (sampleY < yh || sampleY >= yl)
                    continue;

                double majorX = xh + (sampleY - yh) * dxhdy;
                double minorX = sampleY < ym
                    ? xm + (sampleY - yh) * dxmdy
                    : xl + (sampleY - ym) * dxldy;
                double left = flip ? majorX : minorX;
                double right = flip ? minorX : majorX;
                if (right < left)
                {
                    double tmp = left;
                    left = right;
                    right = tmp;
                }

                int firstX = Math.Max(Math.Max(0, _rdpScissorX0), (int)Math.Floor(left));
                int lastX = Math.Min(Math.Min((int)_rdpColorImageWidth - 1, _rdpScissorX1), (int)Math.Ceiling(right) - 1);
                if (lastX < firstX)
                    continue;

                long yStep = (long)Math.Round(sampleY - yh);
                long rowR = shade.R + yStep * (long)shade.DrDe;
                long rowG = shade.G + yStep * (long)shade.DgDe;
                long rowB = shade.B + yStep * (long)shade.DbDe;
                long rowA = shade.A + yStep * (long)shade.DaDe;
                long rowZ = useDepth ? RdpDepthRowStart(depth, yStep) : 0;
                long rowWrittenPixels = 0;
                for (int x = firstX; x <= lastX; x++)
                {
                    long xStep = (long)Math.Round(x + 0.5 - left);
                    if (useDepth && !PassRdpDepthTest((uint)y * _rdpColorImageWidth + (uint)x, rowZ + xStep * (long)depth.DzDx, depth.DzPix))
                        continue;

                    uint rgba = RdpShadeToRgba(
                        rowR + xStep * (long)shade.DrDx,
                        rowG + xStep * (long)shade.DgDx,
                        rowB + xStep * (long)shade.DbDx,
                        rowA + xStep * (long)shade.DaDx);
                    uint address = _rdpColorImageAddress + (((uint)y * _rdpColorImageWidth + (uint)x) * bytesPerPixel);
                    WriteRdpRgbaPixel(address, rgba, bytesPerPixel);
                    wroteAny = true;
                    rowWrittenPixels++;
                }

                writtenPixels += rowWrittenPixels;
                if (rowWrittenPixels > 0)
                {
                    uint rowStart = _rdpColorImageAddress + (((uint)y * _rdpColorImageWidth + (uint)firstX) * bytesPerPixel);
                    NoteRdramWriteRange(rowStart, (uint)(lastX - firstX + 1) * bytesPerPixel);
                }
            }

            NoteRdpPixelWrites(writtenPixels, writtenPixels);
            if (wroteAny)
            {
                MarkRdpColorImageWritten(bytesPerPixel);
                if (writtenPixels >= 256)
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, writtenPixels);
                else if (writtenPixels >= 32 && HasRdpVisibleFramebufferContent(bytesPerPixel, 512u))
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, 512);
            }
            return wroteAny;
        }

        private uint SelectRdpTriangleColor(int command, uint commandAddress, bool xbusDmem)
        {
            if ((command & 0x04) != 0 && TryReadRdpShadeColor(commandAddress, xbusDmem, out uint shade))
                return shade;

            uint color = SelectRdpSolidColor();
            if ((color & 0xFFFFFF00u) == 0 && _rdpFillColor != 0)
                color = Rgba5551ToRgba8888((ushort)(_rdpFillColor >> 16));
            return color;
        }

        private bool TryReadRdpShadeColor(uint commandAddress, bool xbusDmem, out uint rgba)
        {
            if (!TryReadRdpShadeCoefficients(commandAddress, xbusDmem, out RdpTriangleShadeCoefficients shade))
            {
                rgba = 0;
                return false;
            }

            rgba = RdpShadeToRgba(shade.R, shade.G, shade.B, shade.A);
            return (rgba & 0xFFFFFF00u) != 0;
        }

        private bool TryReadRdpShadeCoefficients(uint commandAddress, bool xbusDmem, out RdpTriangleShadeCoefficients coefficients)
        {
            ulong shade0 = ReadRdpCommandDoubleWord(commandAddress + 32u, xbusDmem);
            ulong shade1 = ReadRdpCommandDoubleWord(commandAddress + 40u, xbusDmem);
            ulong shade2 = ReadRdpCommandDoubleWord(commandAddress + 48u, xbusDmem);
            ulong shade3 = ReadRdpCommandDoubleWord(commandAddress + 56u, xbusDmem);
            ulong shade4 = ReadRdpCommandDoubleWord(commandAddress + 64u, xbusDmem);
            ulong shade6 = ReadRdpCommandDoubleWord(commandAddress + 80u, xbusDmem);

            coefficients = new RdpTriangleShadeCoefficients
            {
                R = RdpPackedTextureComponent(shade0, shade2, 32, 48),
                G = RdpPackedTextureComponent(shade0, shade2, 16, 32),
                B = RdpPackedTextureComponent(shade0, shade2, 0, 16),
                A = RdpPackedAlphaComponent(shade0, shade2),
                DrDx = RdpPackedTextureComponent(shade1, shade3, 32, 48),
                DgDx = RdpPackedTextureComponent(shade1, shade3, 16, 32),
                DbDx = RdpPackedTextureComponent(shade1, shade3, 0, 16),
                DaDx = RdpPackedAlphaComponent(shade1, shade3),
                DrDe = RdpPackedTextureComponent(shade4, shade6, 32, 48),
                DgDe = RdpPackedTextureComponent(shade4, shade6, 16, 32),
                DbDe = RdpPackedTextureComponent(shade4, shade6, 0, 16),
                DaDe = RdpPackedAlphaComponent(shade4, shade6)
            };
            return true;
        }

        private static int RdpPackedAlphaComponent(ulong highWord, ulong lowWord)
        {
            return unchecked((int)(((highWord << 16) & 0xFFFF0000UL) | (lowWord & 0x0000FFFFUL)));
        }

        private static uint RdpShadeToRgba(long rFixed, long gFixed, long bFixed, long aFixed)
        {
            uint r = RdpShadeFixedComponentTo8(rFixed);
            uint g = RdpShadeFixedComponentTo8(gFixed);
            uint b = RdpShadeFixedComponentTo8(bFixed);
            uint a = RdpShadeFixedComponentTo8(aFixed);
            return (r << 24) | (g << 16) | (b << 8) | (a == 0 ? 0xFFu : a);
        }

        private static uint RdpShadeFixedComponentTo8(long value)
        {
            long component = value >> 14;
            if (component <= 0)
                return 0;
            return (uint)Math.Min(0xFFL, component);
        }

        private static uint ModulateRdpRgba(uint texel, uint shade)
        {
            uint r = (((texel >> 24) & 0xFFu) * ((shade >> 24) & 0xFFu) + 0x80u) / 0xFFu;
            uint g = (((texel >> 16) & 0xFFu) * ((shade >> 16) & 0xFFu) + 0x80u) / 0xFFu;
            uint b = (((texel >> 8) & 0xFFu) * ((shade >> 8) & 0xFFu) + 0x80u) / 0xFFu;
            uint a = ((texel & 0xFFu) * (shade & 0xFFu) + 0x80u) / 0xFFu;
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        private uint ApplyRdpColorCombiner(uint texel0, uint shade)
        {
            uint combined = EvaluateRdpCombineCycle(
                _rdpCombine.SubARgb0,
                _rdpCombine.SubBRgb0,
                _rdpCombine.MulRgb0,
                _rdpCombine.AddRgb0,
                _rdpCombine.SubAA0,
                _rdpCombine.SubBA0,
                _rdpCombine.MulA0,
                _rdpCombine.AddA0,
                0u,
                texel0,
                0u,
                shade);

            if (_rdpOtherModesCycleType == 1u)
            {
                combined = EvaluateRdpCombineCycle(
                    _rdpCombine.SubARgb1,
                    _rdpCombine.SubBRgb1,
                    _rdpCombine.MulRgb1,
                    _rdpCombine.AddRgb1,
                    _rdpCombine.SubAA1,
                    _rdpCombine.SubBA1,
                    _rdpCombine.MulA1,
                    _rdpCombine.AddA1,
                    combined,
                    texel0,
                    0u,
                    shade);
            }

            if ((combined & 0xFFFFFF00u) == 0u && (texel0 & 0xFFFFFF00u) != 0u)
                return shade == 0xFFFFFFFFu ? texel0 : ModulateRdpRgba(texel0, shade);

            return combined;
        }

        private uint EvaluateRdpCombineCycle(
            int subRgbA,
            int subRgbB,
            int mulRgb,
            int addRgb,
            int subAlphaA,
            int subAlphaB,
            int mulAlpha,
            int addAlpha,
            uint combined,
            uint texel0,
            uint texel1,
            uint shade)
        {
            uint a = RdpRgbSubAInput(subRgbA, combined, texel0, texel1, shade);
            uint b = RdpRgbSubBInput(subRgbB, combined, texel0, texel1, shade);
            uint c = RdpRgbMulInput(mulRgb, combined, texel0, texel1, shade);
            uint d = RdpRgbAddInput(addRgb, combined, texel0, texel1, shade);

            uint r = RdpCombineChannel((a >> 24) & 0xFFu, (b >> 24) & 0xFFu, (c >> 24) & 0xFFu, (d >> 24) & 0xFFu);
            uint g = RdpCombineChannel((a >> 16) & 0xFFu, (b >> 16) & 0xFFu, (c >> 16) & 0xFFu, (d >> 16) & 0xFFu);
            uint bch = RdpCombineChannel((a >> 8) & 0xFFu, (b >> 8) & 0xFFu, (c >> 8) & 0xFFu, (d >> 8) & 0xFFu);
            uint alpha = RdpCombineChannel(
                RdpAlphaInput(subAlphaA, combined, texel0, texel1, shade),
                RdpAlphaInput(subAlphaB, combined, texel0, texel1, shade),
                RdpAlphaInput(mulAlpha, combined, texel0, texel1, shade),
                RdpAlphaInput(addAlpha, combined, texel0, texel1, shade));

            return (r << 24) | (g << 16) | (bch << 8) | alpha;
        }

        private uint RdpRgbSubAInput(int source, uint combined, uint texel0, uint texel1, uint shade)
        {
            switch (source & 0xF)
            {
                case 0:
                    return combined;
                case 1:
                    return texel0;
                case 2:
                    return texel1;
                case 3:
                    return _rdpPrimColor;
                case 4:
                    return shade;
                case 5:
                    return _rdpEnvColor;
                case 6:
                    return 0xFFFFFFFFu;
                default:
                    return 0u;
            }
        }

        private uint RdpRgbSubBInput(int source, uint combined, uint texel0, uint texel1, uint shade)
        {
            switch (source & 0xF)
            {
                case 0:
                    return combined;
                case 1:
                    return texel0;
                case 2:
                    return texel1;
                case 3:
                    return _rdpPrimColor;
                case 4:
                    return shade;
                case 5:
                    return _rdpEnvColor;
                default:
                    return 0u;
            }
        }

        private uint RdpRgbMulInput(int source, uint combined, uint texel0, uint texel1, uint shade)
        {
            switch (source & 0x1F)
            {
                case 0:
                    return combined;
                case 1:
                    return texel0;
                case 2:
                    return texel1;
                case 3:
                    return _rdpPrimColor;
                case 4:
                    return shade;
                case 5:
                    return _rdpEnvColor;
                case 7:
                    return RdpAlphaAsRgb(combined);
                case 8:
                    return RdpAlphaAsRgb(texel0);
                case 9:
                    return RdpAlphaAsRgb(texel1);
                case 10:
                    return RdpAlphaAsRgb(_rdpPrimColor);
                case 11:
                    return RdpAlphaAsRgb(shade);
                case 12:
                    return RdpAlphaAsRgb(_rdpEnvColor);
                default:
                    return 0u;
            }
        }

        private uint RdpRgbAddInput(int source, uint combined, uint texel0, uint texel1, uint shade)
        {
            switch (source & 0x7)
            {
                case 0:
                    return combined;
                case 1:
                    return texel0;
                case 2:
                    return texel1;
                case 3:
                    return _rdpPrimColor;
                case 4:
                    return shade;
                case 5:
                    return _rdpEnvColor;
                case 6:
                    return 0xFFFFFFFFu;
                default:
                    return 0u;
            }
        }

        private uint RdpAlphaInput(int source, uint combined, uint texel0, uint texel1, uint shade)
        {
            switch (source & 0x7)
            {
                case 0:
                    return combined & 0xFFu;
                case 1:
                    return texel0 & 0xFFu;
                case 2:
                    return texel1 & 0xFFu;
                case 3:
                    return _rdpPrimColor & 0xFFu;
                case 4:
                    return shade & 0xFFu;
                case 5:
                    return _rdpEnvColor & 0xFFu;
                case 6:
                    return 0xFFu;
                default:
                    return 0u;
            }
        }

        private static uint RdpAlphaAsRgb(uint rgba)
        {
            uint a = rgba & 0xFFu;
            return (a << 24) | (a << 16) | (a << 8) | a;
        }

        private static uint RdpCombineChannel(uint a, uint b, uint c, uint d)
        {
            int value = ((((int)a - (int)b) * (int)c) + ((int)d << 8) + 0x80) >> 8;
            if (value <= 0)
                return 0u;
            if (value >= 0xFF)
                return 0xFFu;
            return (uint)value;
        }

        private static double RdpTriangleYToScreen(uint value)
        {
            return SignExtend14(value) / 4.0;
        }

        private static double RdpTriangleXToScreen(uint value)
        {
            return SignExtend30(value) / 65536.0;
        }

        private static double RdpTriangleDeltaXToScreen(uint value)
        {
            return unchecked((int)value) / 65536.0;
        }

        private static int SignExtend14(uint value)
        {
            int signed = (int)(value & 0x3FFFu);
            if ((signed & 0x2000) != 0)
                signed -= 0x4000;
            return signed;
        }

        private static int SignExtend17(int value)
        {
            int signed = value & 0x1FFFF;
            if ((signed & 0x10000) != 0)
                signed -= 0x20000;
            return signed;
        }

        private static int SignExtend30(uint value)
        {
            int signed = (int)(value & 0x3FFFFFFFu);
            if ((signed & 0x20000000) != 0)
                signed = unchecked((int)(signed | 0xC0000000u));
            return signed;
        }

        private static uint RdpBytesPerPixel(uint rdpSize)
        {
            switch (rdpSize & 0x3u)
            {
                case 0u:
                case 1u:
                    return 1u;
                case 2u:
                    return 2u;
                case 3u:
                    return 4u;
                default:
                    return 0u;
            }
        }

        private static uint RdpPackedBytesForTexels(uint texels, uint rdpSize)
        {
            switch (rdpSize & 0x3u)
            {
                case 0u:
                    return (texels + 1u) >> 1;
                case 1u:
                    return texels;
                case 2u:
                    return texels * 2u;
                case 3u:
                    return texels * 4u;
                default:
                    return 0u;
            }
        }

        private void ExecuteRdpSetTile(uint w0, uint w1)
        {
            int tileIndex = (int)((w1 >> 24) & 0x7u);
            uint format = (w0 >> 21) & 0x7u;
            uint size = (w0 >> 19) & 0x3u;
            if (format == 4u && size > 1u)
                format = 0u;
            if (format == 2u && size > 1u)
                format = 0u;
            if (format == 0u && size < 2u)
                format = 2u;

            _rdpTiles[tileIndex].Format = format;
            _rdpTiles[tileIndex].Size = size;
            _rdpTiles[tileIndex].Line = (w0 >> 9) & 0x1FFu;
            _rdpTiles[tileIndex].Tmem = w0 & 0x1FFu;
            _rdpTiles[tileIndex].Palette = (w1 >> 20) & 0xFu;
            _rdpTiles[tileIndex].ClampT = ((w1 >> 19) & 1u) != 0;
            _rdpTiles[tileIndex].MirrorT = ((w1 >> 18) & 1u) != 0;
            _rdpTiles[tileIndex].MaskT = (w1 >> 14) & 0xFu;
            _rdpTiles[tileIndex].ShiftT = (w1 >> 10) & 0xFu;
            _rdpTiles[tileIndex].ClampS = ((w1 >> 9) & 1u) != 0;
            _rdpTiles[tileIndex].MirrorS = ((w1 >> 8) & 1u) != 0;
            _rdpTiles[tileIndex].MaskS = (w1 >> 4) & 0xFu;
            _rdpTiles[tileIndex].ShiftS = w1 & 0xFu;
        }

        private void ExecuteRdpSetOtherModes(uint w0, uint w1)
        {
            ulong mode = ((ulong)w0 << 32) | w1;
            _rdpOtherModesCycleType = (uint)((mode >> 52) & 0x3UL);
            _rdpOtherModesEnableTlut = ((mode >> 47) & 1UL) != 0;
            _rdpOtherModesTlutType = ((mode >> 46) & 1UL) != 0;
            _rdpOtherModesPerspectiveTexture = ((mode >> 51) & 1UL) != 0;
            _rdpOtherModesForceBlend = ((mode >> 14) & 1UL) != 0;
            _rdpOtherModesZMode = (uint)((mode >> 10) & 0x3UL);
            _rdpOtherModesCvgDest = (uint)((mode >> 8) & 0x3UL);
            _rdpOtherModesImageRead = ((mode >> 6) & 1UL) != 0;
            _rdpOtherModesZUpdate = ((mode >> 5) & 1UL) != 0;
            _rdpOtherModesZCompare = ((mode >> 4) & 1UL) != 0;
            _rdpOtherModesZSourceSel = ((mode >> 2) & 1UL) != 0;
            _rdpOtherModesAlphaCompare = (mode & 1UL) != 0;
        }

        private void ExecuteRdpSetScissor(uint w0, uint w1)
        {
            _rdpScissorX0 = (int)(((w0 >> 12) & 0x0FFFu) >> 2);
            _rdpScissorY0 = (int)((w0 & 0x0FFFu) >> 2);
            _rdpScissorX1 = RdpScissorMaxToInclusive((w1 >> 12) & 0x0FFFu);
            _rdpScissorY1 = RdpScissorMaxToInclusive(w1 & 0x0FFFu);

            if (_rdpScissorX1 < _rdpScissorX0)
                _rdpScissorX1 = _rdpScissorX0 - 1;
            if (_rdpScissorY1 < _rdpScissorY0)
                _rdpScissorY1 = _rdpScissorY0 - 1;
        }

        private static int RdpScissorMaxToInclusive(uint raw)
        {
            return (int)((raw + 3u) >> 2) - 1;
        }

        private void ExecuteRdpSetCombine(uint w0, uint w1)
        {
            ulong mode = ((ulong)w0 << 32) | w1;
            _rdpCombine.SubARgb0 = (int)((mode >> 52) & 0xFu);
            _rdpCombine.MulRgb0 = (int)((mode >> 47) & 0x1Fu);
            _rdpCombine.SubAA0 = (int)((mode >> 44) & 0x7u);
            _rdpCombine.MulA0 = (int)((mode >> 41) & 0x7u);
            _rdpCombine.SubARgb1 = (int)((mode >> 37) & 0xFu);
            _rdpCombine.MulRgb1 = (int)((mode >> 32) & 0x1Fu);
            _rdpCombine.SubBRgb0 = (int)((mode >> 28) & 0xFu);
            _rdpCombine.SubBRgb1 = (int)((mode >> 24) & 0xFu);
            _rdpCombine.SubAA1 = (int)((mode >> 21) & 0x7u);
            _rdpCombine.MulA1 = (int)((mode >> 18) & 0x7u);
            _rdpCombine.AddRgb0 = (int)((mode >> 15) & 0x7u);
            _rdpCombine.SubBA0 = (int)((mode >> 12) & 0x7u);
            _rdpCombine.AddA0 = (int)((mode >> 9) & 0x7u);
            _rdpCombine.AddRgb1 = (int)((mode >> 6) & 0x7u);
            _rdpCombine.SubBA1 = (int)((mode >> 3) & 0x7u);
            _rdpCombine.AddA1 = (int)(mode & 0x7u);
            _rdpCombineModeSet = true;
        }

        private void ExecuteRdpSetTileSize(uint w0, uint w1)
        {
            int tileIndex = (int)((w1 >> 24) & 0x7u);
            _rdpTiles[tileIndex].Uls = (w0 >> 12) & 0x0FFFu;
            _rdpTiles[tileIndex].Ult = w0 & 0x0FFFu;
            _rdpTiles[tileIndex].Lrs = (w1 >> 12) & 0x0FFFu;
            _rdpTiles[tileIndex].Lrt = w1 & 0x0FFFu;
            _rdpTiles[tileIndex].TileSizeSet = true;
        }

        private void ExecuteRdpLoadTlut(uint w0, uint w1)
        {
            if (_rdpTextureImageAddress >= RDRAM.Length)
                return;

            int tileIndex = (int)((w1 >> 24) & 0x7u);
            uint sl = ((w0 >> 12) & 0x0FFFu) >> 2;
            uint tl = (w0 & 0x0FFFu) >> 2;
            uint sh = ((w1 >> 12) & 0x0FFFu) >> 2;
            if (sh < sl)
                return;

            uint entries = Math.Min(256u, sh - sl + 1u);
            uint sourceRowBytes = RdpPackedBytesForTexels(_rdpTextureImageWidth == 0 ? 1u : _rdpTextureImageWidth, _rdpTextureImageSize);
            uint sourceAddress = _rdpTextureImageAddress + tl * sourceRowBytes + sl * 2u;
            uint paletteOffset = Math.Min(255u, _rdpTiles[tileIndex].Palette * 16u);
            for (uint i = 0; i < entries; i++)
            {
                uint address = sourceAddress + i * 2u;
                if (address + 1u >= RDRAM.Length || paletteOffset + i >= _rdpTlut.Length)
                    break;

                ushort color = (ushort)((RDRAM[address] << 8) | RDRAM[address + 1u]);
                _rdpTlut[paletteOffset + i] = color;

                uint tmemWord = (_rdpTiles[tileIndex].Tmem << 2) + i * 4u;
                uint tmemAddress = tmemWord * 2u;
                for (uint replicate = 0; replicate < 4u && tmemAddress + 1u < _rdpTmem.Length; replicate++, tmemAddress += 2u)
                {
                    _rdpTmem[tmemAddress] = (byte)(color >> 8);
                    _rdpTmem[tmemAddress + 1u] = (byte)color;
                }
            }
        }

        private void ExecuteRdpLoadBlock(uint w0, uint w1)
        {
            if (_rdpTextureImageAddress >= RDRAM.Length)
                return;

            int tileIndex = (int)((w1 >> 24) & 0x7u);
            uint texels = ((w1 >> 12) & 0x0FFFu) + 1u;
            uint byteCount = RdpPackedBytesForTexels(texels, _rdpTextureImageSize);
            CopyTextureImageToTmem(tileIndex, 0u, byteCount);
            TraceRdpTextureLoad("loadblock", tileIndex, w0, w1, byteCount);
        }

        private void ExecuteRdpLoadTile(uint w0, uint w1)
        {
            if (_rdpTextureImageAddress >= RDRAM.Length || _rdpTextureImageWidth == 0)
                return;

            int tileIndex = (int)((w1 >> 24) & 0x7u);
            uint uls = ((w0 >> 12) & 0x0FFFu) >> 2;
            uint ult = (w0 & 0x0FFFu) >> 2;
            uint lrs = ((w1 >> 12) & 0x0FFFu) >> 2;
            uint lrt = (w1 & 0x0FFFu) >> 2;
            if (lrs < uls || lrt < ult)
                return;

            uint width = lrs - uls + 1u;
            uint height = lrt - ult + 1u;
            uint sourceRowBytes = RdpPackedBytesForTexels(_rdpTextureImageWidth, _rdpTextureImageSize);
            uint destinationStride = GetTileStrideBytes(_rdpTiles[tileIndex], width);
            uint destinationBase = Math.Min(4095u, _rdpTiles[tileIndex].Tmem * 8u);
            for (uint y = 0; y < height; y++)
            {
                uint sourceOffset = ((ult + y) * sourceRowBytes) + RdpPackedBytesForTexels(uls, _rdpTextureImageSize);
                uint sourceAddress = _rdpTextureImageAddress + sourceOffset;
                uint rowBytes = RdpPackedBytesForTexels(width, _rdpTextureImageSize);
                uint destinationOffset = destinationBase + y * destinationStride;
                CopyRdramToTmem(sourceAddress, destinationOffset, rowBytes);
            }
            TraceRdpTextureLoad("loadtile", tileIndex, w0, w1, height * RdpPackedBytesForTexels(width, _rdpTextureImageSize));
        }

        private void CopyTextureImageToTmem(int tileIndex, uint sourceOffset, uint byteCount)
        {
            uint sourceAddress = _rdpTextureImageAddress + sourceOffset;
            uint destinationOffset = Math.Min(4095u, _rdpTiles[tileIndex].Tmem * 8u);
            CopyRdramToTmem(sourceAddress, destinationOffset, byteCount);
        }

        private void CopyRdramToTmem(uint sourceAddress, uint destinationOffset, uint byteCount)
        {
            if (sourceAddress >= RDRAM.Length || destinationOffset >= _rdpTmem.Length || byteCount == 0)
                return;

            uint availableSource = (uint)RDRAM.Length - sourceAddress;
            uint availableDestination = (uint)_rdpTmem.Length - destinationOffset;
            uint count = Math.Min(byteCount, Math.Min(availableSource, availableDestination));
            Array.Copy(RDRAM, (int)sourceAddress, _rdpTmem, (int)destinationOffset, (int)count);
        }

        private void TraceRdpTextureLoad(string op, int tileIndex, uint w0, uint w1, uint byteCount)
        {
            if (!TraceRdpTextureRectangles || _traceRdpTextureLoadCount >= TraceRdpTextureLoadLimit)
                return;

            uint tmemOffset = Math.Min(4095u, _rdpTiles[tileIndex].Tmem * 8u);
            byte b0 = tmemOffset + 0u < _rdpTmem.Length ? _rdpTmem[tmemOffset + 0u] : (byte)0;
            byte b1 = tmemOffset + 1u < _rdpTmem.Length ? _rdpTmem[tmemOffset + 1u] : (byte)0;
            byte b2 = tmemOffset + 2u < _rdpTmem.Length ? _rdpTmem[tmemOffset + 2u] : (byte)0;
            byte b3 = tmemOffset + 3u < _rdpTmem.Length ? _rdpTmem[tmemOffset + 3u] : (byte)0;
            Common.Logger.PrintWarningLine(
                $"[N64RDP] {op} tile={tileIndex} ti=0x{_rdpTextureImageAddress:x8}/{_rdpTextureImageFormat}:{_rdpTextureImageSize}x{_rdpTextureImageWidth} " +
                $"tmem=0x{_rdpTiles[tileIndex].Tmem:x} line={_rdpTiles[tileIndex].Line} bytes={byteCount} first={b0:x2}{b1:x2}{b2:x2}{b3:x2} w0=0x{w0:x8} w1=0x{w1:x8}");
            _traceRdpTextureLoadCount++;
        }

        private void ExecuteRdpTextureRectangle(int command, uint w0, uint w1, uint w2, uint w3)
        {
            uint bytesPerPixel = RdpBytesPerPixel(_rdpColorImageSize);
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0)
                return;

            uint rawX1 = (w0 >> 12) & 0x0FFFu;
            uint rawY1 = w0 & 0x0FFFu;
            if (UseReferenceTexRectExtents && (_rdpOtherModesCycleType == 2u || _rdpOtherModesCycleType == 3u))
                rawY1 |= 3u;

            uint x1 = rawX1 >> 2;
            uint y1 = rawY1 >> 2;
            uint x0 = ((w1 >> 12) & 0x0FFFu) >> 2;
            uint y0 = (w1 & 0x0FFFu) >> 2;
            if (x1 < x0)
            {
                uint temp = x0;
                x0 = x1;
                x1 = temp;
            }
            if (y1 < y0)
            {
                uint temp = y0;
                y0 = y1;
                y1 = temp;
            }

            if (TraceRdpTextureRectangles && _traceRdpTexRectCount < TraceRdpTexRectLimit)
            {
                int tileIndex = (int)((w1 >> 24) & 0x7u);
                Common.Logger.PrintWarningLine(
                    $"[N64RDP] texrect cmd=0x{command:x2} ci=0x{_rdpColorImageAddress:x8} size={_rdpColorImageSize} width={_rdpColorImageWidth} " +
                    $"rect=({x0},{y0})-({x1},{y1}) tile={tileIndex} ti=0x{_rdpTextureImageAddress:x8}/{_rdpTextureImageFormat}:{_rdpTextureImageSize}x{_rdpTextureImageWidth} " +
                    $"tmem=0x{_rdpTiles[tileIndex].Tmem:x} line={_rdpTiles[tileIndex].Line} fmt={_rdpTiles[tileIndex].Format}:{_rdpTiles[tileIndex].Size} " +
                    $"w0=0x{w0:x8} w1=0x{w1:x8} w2=0x{w2:x8} w3=0x{w3:x8}");
                _traceRdpTexRectCount++;
            }

            bool wrote = DrawRdpTexturedRectangle(x0, y0, x1, y1, (int)((w1 >> 24) & 0x7u), w2, w3, command == 0x25, bytesPerPixel);
            if (!wrote && EnableTexRectSolidFallback)
                DrawRdpSolidRectangle(x0, y0, x1, y1, SelectRdpSolidColor(), bytesPerPixel);
        }

        private void ExecuteRdpFillRectangle(uint w0, uint w1)
        {
            uint bytesPerPixel = RdpBytesPerPixel(_rdpColorImageSize);
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0)
                return;

            uint x1 = ((w0 >> 12) & 0x0FFFu) >> 2;
            uint y1 = (w0 & 0x0FFFu) >> 2;
            uint x0 = ((w1 >> 12) & 0x0FFFu) >> 2;
            uint y0 = (w1 & 0x0FFFu) >> 2;
            if (x1 < x0)
            {
                uint temp = x0;
                x0 = x1;
                x1 = temp;
            }
            if (y1 < y0)
            {
                uint temp = y0;
                y0 = y1;
                y1 = temp;
            }

            DrawRdpFillRectangle(x0, y0, x1, y1, bytesPerPixel);
        }

        private void DrawRdpFillRectangle(uint x0, uint y0, uint x1, uint y1, uint bytesPerPixel)
        {
            if (!ClampRdpRectangleToScissor(ref x0, ref y0, ref x1, ref y1))
                return;
            if (x0 >= _rdpColorImageWidth)
                return;
            if (x1 >= _rdpColorImageWidth)
                x1 = _rdpColorImageWidth - 1u;

            uint maxRows = (uint)((RDRAM.Length - _rdpColorImageAddress) / (_rdpColorImageWidth * bytesPerPixel));
            if (maxRows == 0 || y0 >= maxRows)
                return;
            if (y1 >= maxRows)
                y1 = maxRows - 1u;

            if (TraceRdpCommands && _traceRdpFillRectCount < TraceRdpFillRectLimit)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64RDP] fillrect ci=0x{_rdpColorImageAddress:x8} size={_rdpColorImageSize} width={_rdpColorImageWidth} " +
                    $"rect=({x0},{y0})-({x1},{y1}) fill=0x{_rdpFillColor:x8} visible={IsRdpFillColorRgbNonZero(bytesPerPixel)}");
                _traceRdpFillRectCount++;
            }

            for (uint y = y0; y <= y1; y++)
            {
                uint rowStart = _rdpColorImageAddress + ((y * _rdpColorImageWidth + x0) * bytesPerPixel);
                uint rowPixels = x1 - x0 + 1u;
                for (uint x = 0; x < rowPixels; x++)
                {
                    uint pixelIndex = y * _rdpColorImageWidth + x0 + x;
                    uint address = _rdpColorImageAddress + pixelIndex * bytesPerPixel;
                    WriteRdpFillPixel(address, pixelIndex, bytesPerPixel);
                }
                NoteRdramWriteRange(rowStart, rowPixels * bytesPerPixel);
            }

            long pixels = (long)(y1 - y0 + 1u) * (x1 - x0 + 1u);
            bool visibleFill = IsRdpFillColorRgbNonZero(bytesPerPixel);
            NoteRdpPixelWrites(pixels, visibleFill ? pixels : 0);
            MarkRdpColorImageWritten(bytesPerPixel);
            if (visibleFill && _rdpColorImageAddress >= 0x00300000u)
            {
                if (pixels >= 256)
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, pixels);
                else if (HasRdpVisibleFramebufferContent(bytesPerPixel, 512u))
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, 512);
            }
        }

        private bool DrawRdpTexturedRectangle(uint x0, uint y0, uint x1, uint y1, int tileIndex, uint w2, uint w3, bool flip, uint bytesPerPixel)
        {
            long perfStart = StartPerfTimer();
            try
            {
            uint textureOriginX = x0;
            uint textureOriginY = y0;
            if (!ClampRdpRectangleToScissor(ref x0, ref y0, ref x1, ref y1))
                return false;
            if ((uint)tileIndex >= _rdpTiles.Length || x0 >= _rdpColorImageWidth)
                return false;
            if (x1 >= _rdpColorImageWidth)
                x1 = _rdpColorImageWidth - 1u;

            uint maxRows = (uint)((RDRAM.Length - _rdpColorImageAddress) / (_rdpColorImageWidth * bytesPerPixel));
            if (maxRows == 0 || y0 >= maxRows)
                return false;
            if (y1 >= maxRows)
                y1 = maxRows - 1u;

            RdpTileState tile = _rdpTiles[tileIndex];
            int sFixed = (short)(w2 >> 16);
            int tFixed = (short)w2;
            int dsdxFixed = (short)(w3 >> 16);
            int dtdyFixed = (short)w3;
            double startS = sFixed / 32.0;
            double startT = tFixed / 32.0;
            double stepS = dsdxFixed == 0 ? 1.0 : dsdxFixed / 1024.0;
            double stepT = dtdyFixed == 0 ? 1.0 : dtdyFixed / 1024.0;
            bool wroteAny = false;
            uint sampleMisses = 0;
            uint sampleHits = 0;
            uint nonZeroSampleHits = 0;
            uint firstAddress = 0;
            uint firstRgba = 0;
            uint firstNonZeroAddress = 0;
            uint firstNonZeroRgba = 0;

            for (uint y = y0; y <= y1; y++)
            {
                uint rowStart = _rdpColorImageAddress + ((y * _rdpColorImageWidth + x0) * bytesPerPixel);
                uint rowPixels = x1 - x0 + 1u;
                bool rowWrote = false;
                for (uint x = 0; x < rowPixels; x++)
                {
                    uint screenX = x0 + x;
                    uint dx = screenX - textureOriginX;
                    uint dy = y - textureOriginY;
                    int sampleS;
                    int sampleT;
                    if (flip && UseReferenceTexRectFlip)
                    {
                        long s1024 = (long)sFixed * 32L + (long)dy * dtdyFixed;
                        long t1024 = (long)tFixed * 32L + (long)dx * dsdxFixed;
                        sampleS = RdpTexRectFixedToTexel(s1024);
                        sampleT = RdpTexRectFixedToTexel(t1024);
                    }
                    else
                    {
                        sampleS = (int)Math.Floor(startS + dx * stepS);
                        sampleT = (int)Math.Floor(startT + dy * stepT);
                    }

                    if (!SampleRdpTexture(tile, sampleS, sampleT, out uint rgba))
                    {
                        sampleMisses++;
                        continue;
                    }
                    if (ShouldRejectRdpAlpha(rgba))
                    {
                        sampleHits++;
                        continue;
                    }

                    uint address = _rdpColorImageAddress + ((y * _rdpColorImageWidth + screenX) * bytesPerPixel);
                    if (!wroteAny)
                    {
                        firstAddress = address;
                        firstRgba = rgba;
                    }
                    WriteRdpRgbaPixel(address, rgba, bytesPerPixel);
                    sampleHits++;
                    rowWrote = true;
                    if (IsRgbaNonZero(rgba))
                    {
                        if (firstNonZeroAddress == 0)
                        {
                            firstNonZeroAddress = address;
                            firstNonZeroRgba = rgba;
                        }
                        nonZeroSampleHits++;
                    }
                    wroteAny = true;
                }

                if (rowWrote)
                    NoteRdramWriteRange(rowStart, rowPixels * bytesPerPixel);
            }

            if (TraceRdpTextureRectangles && _traceRdpTexRectWriteCount < TraceRdpTexRectWriteLimit)
            {
                uint firstStored = firstAddress + 1u < RDRAM.Length
                    ? (uint)((RDRAM[firstAddress] << 8) | RDRAM[firstAddress + 1u])
                    : 0u;
                uint firstNonZeroStored = firstNonZeroAddress + 1u < RDRAM.Length
                    ? (uint)((RDRAM[firstNonZeroAddress] << 8) | RDRAM[firstNonZeroAddress + 1u])
                    : 0u;
                Common.Logger.PrintWarningLine(
                    $"[N64RDP] texrect-write ci=0x{_rdpColorImageAddress:x8} rect=({x0},{y0})-({x1},{y1}) tile={tileIndex} " +
                    $"flip={flip} hits={sampleHits} nz={nonZeroSampleHits} misses={sampleMisses} firstAddr=0x{firstAddress:x8} firstRgba=0x{firstRgba:x8} firstStored=0x{firstStored:x4} " +
                    $"firstNzAddr=0x{firstNonZeroAddress:x8} firstNzRgba=0x{firstNonZeroRgba:x8} firstNzStored=0x{firstNonZeroStored:x4} " +
                    $"tileSizeSet={tile.TileSizeSet} tile=fmt{tile.Format}:sz{tile.Size}:line{tile.Line}:tmem0x{tile.Tmem:x}:uls{tile.Uls}:ult{tile.Ult}:lrs{tile.Lrs}:lrt{tile.Lrt}");
                _traceRdpTexRectWriteCount++;
            }

            NoteRdpPixelWrites(sampleHits, nonZeroSampleHits);
            if (wroteAny)
            {
                MarkRdpColorImageWritten(bytesPerPixel);
                if (nonZeroSampleHits >= 256)
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, nonZeroSampleHits);
                else if (HasRdpVisibleFramebufferContent(bytesPerPixel, 512u))
                    CaptureVisibleRdpFramebufferSnapshot(bytesPerPixel, 512);
            }
            return wroteAny;
            }
            finally
            {
                AddPerfTicks(ref _perfRdpTexturedRectangleTicks, ref _perfRdpTexturedRectangleCalls, perfStart);
            }
        }

        private bool ClampRdpRectangleToScissor(ref uint x0, ref uint y0, ref uint x1, ref uint y1)
        {
            if (_rdpScissorX1 < _rdpScissorX0 || _rdpScissorY1 < _rdpScissorY0)
                return false;

            uint sx0 = (uint)Math.Max(0, _rdpScissorX0);
            uint sy0 = (uint)Math.Max(0, _rdpScissorY0);
            uint sx1 = (uint)_rdpScissorX1;
            uint sy1 = (uint)_rdpScissorY1;
            if (x1 < sx0 || y1 < sy0 || x0 > sx1 || y0 > sy1)
                return false;

            if (x0 < sx0)
                x0 = sx0;
            if (y0 < sy0)
                y0 = sy0;
            if (x1 > sx1)
                x1 = sx1;
            if (y1 > sy1)
                y1 = sy1;

            return x1 >= x0 && y1 >= y0;
        }

        private static int RdpTexRectFixedToTexel(long value1024)
        {
            if (value1024 >= 0)
                return (int)(value1024 >> 10);

            return -(int)((-value1024 + 1023L) >> 10);
        }

        private bool SampleRdpTexture(RdpTileState tile, int s, int t, out uint rgba)
        {
            rgba = 0;
            uint width = GetTileWidth(tile);
            if (width == 0)
                return false;

            uint stride = GetTileStrideBytes(tile, width);
            uint baseOffset = Math.Min(4095u, tile.Tmem * 8u);
            uint height = GetTileHeight(tile, baseOffset, stride);
            if (height == 0)
                return false;

            int u = ApplyRdpTextureCoordinate(s, tile.Uls >> 2, width, tile.MaskS, tile.ShiftS, tile.ClampS, tile.MirrorS);
            int v = ApplyRdpTextureCoordinate(t, tile.Ult >> 2, height, tile.MaskT, tile.ShiftT, tile.ClampT, tile.MirrorT);
            if (u < 0 || v < 0)
                return false;

            return DecodeRdpTextureColor(tile, u, v, out rgba);
        }

        private static int ApplyRdpTextureCoordinate(int value, uint origin, uint extent, uint mask, uint shift, bool clamp, bool mirror)
        {
            if (extent == 0)
                return -1;
            if (shift != 0)
                value = shift < 11u ? value >> (int)shift : value << (int)(16u - shift);
            value -= (int)origin;

            if (clamp)
            {
                if (value < 0)
                    return 0;
                if ((uint)value >= extent)
                    return (int)extent - 1;
                return value;
            }

            if (mask != 0 && mask < 31)
            {
                int maskValue = (1 << (int)mask) - 1;
                if (!mirror)
                    return value & maskValue;

                int mirrored = value & ((1 << ((int)mask + 1)) - 1);
                return (mirrored & (maskValue + 1)) == 0
                    ? mirrored & maskValue
                    : (~mirrored) & maskValue;
            }

            if (value < 0)
                return 0;
            if ((uint)value >= extent)
                return (int)extent - 1;
            return value;
        }

        private static uint GetTileWidth(RdpTileState tile)
        {
            if (tile.TileSizeSet && tile.Lrs >= tile.Uls)
            {
                uint width = ((tile.Lrs - tile.Uls) >> 2) + 1u;
                if (width != 0)
                    return width;
            }

            if (tile.Line == 0)
                return 1u;

            uint stride = tile.Line * 8u;
            switch (tile.Size & 0x3u)
            {
                case 0u: return Math.Max(1u, stride * 2u);
                case 1u: return Math.Max(1u, stride);
                case 2u: return Math.Max(1u, stride / 2u);
                case 3u: return Math.Max(1u, stride / 4u);
                default: return 1u;
            }
        }

        private static uint GetTileHeight(RdpTileState tile)
        {
            if (tile.TileSizeSet && tile.Lrt >= tile.Ult)
                return ((tile.Lrt - tile.Ult) >> 2) + 1u;
            return 1u;
        }

        private static uint GetTileHeight(RdpTileState tile, uint baseOffset, uint stride)
        {
            uint explicitHeight = GetTileHeight(tile);
            if (tile.TileSizeSet || stride == 0 || baseOffset >= 4096u)
                return explicitHeight;

            // Many sprite-style texrects set only tile line/tmem. Treat the available
            // TMEM rows as the tile extent instead of clamping every sample to row 0.
            return Math.Max(1u, (4096u - baseOffset) / stride);
        }

        private static uint GetTileStrideBytes(RdpTileState tile, uint width)
        {
            if (tile.Line != 0)
                return tile.Line * 8u;
            return Math.Max(1u, RdpPackedBytesForTexels(width, tile.Size));
        }

        private static uint GetPackedTexelOffset(uint x, uint y, uint size, uint stride)
        {
            switch (size & 0x3u)
            {
                case 0u:
                    return y * stride + (x >> 1);
                case 1u:
                    return y * stride + x;
                case 2u:
                    return y * stride + x * 2u;
                case 3u:
                    return y * stride + x * 4u;
                default:
                    return y * stride + x;
            }
        }

        private bool DecodeRdpTextureColor(RdpTileState tile, int s, int t, out uint rgba)
        {
            rgba = 0;
            uint tbase = (tile.Tmem + ((tile.Line * (uint)Math.Max(0, t)) & 0x1FFu)) & 0x1FFu;
            switch (tile.Format & 0x7u)
            {
                case 0u: // RGBA
                    if ((tile.Size & 0x3u) == 2u)
                    {
                        uint tmemOffset = ((tbase << 3) + (uint)Math.Max(0, s) * 2u) & 0xFFFu;
                        if (tmemOffset + 1u >= _rdpTmem.Length)
                            return false;
                        ushort color = ReadRdpTmemWord(tmemOffset);
                        if (_rdpOtherModesEnableTlut)
                            color = LookupRdpTlut((uint)(color >> 8));
                        rgba = _rdpOtherModesEnableTlut ? RdpTlutColorToRgba(color) : Rgba5551ToRgba8888(color);
                        return true;
                    }
                    if ((tile.Size & 0x3u) == 3u)
                    {
                        uint tmemOffset = ((tbase << 3) + (uint)Math.Max(0, s) * 4u) & 0xFFFu;
                        if (tmemOffset + 3u >= _rdpTmem.Length)
                            return false;
                        rgba = ((uint)_rdpTmem[tmemOffset] << 24)
                            | ((uint)_rdpTmem[tmemOffset + 1u] << 16)
                            | ((uint)_rdpTmem[tmemOffset + 2u] << 8)
                            | _rdpTmem[tmemOffset + 3u];
                        return true;
                    }
                    return false;
                case 2u: // CI
                {
                    uint sUnsigned = (uint)Math.Max(0, s);
                    uint index;
                    if ((tile.Size & 0x3u) == 0u)
                    {
                        uint tmemOffset = ((tbase << 3) + (sUnsigned >> 1)) & 0xFFFu;
                        byte packed = _rdpTmem[tmemOffset];
                        index = (sUnsigned & 1u) == 0u ? (uint)(packed >> 4) : (uint)(packed & 0x0F);
                        index += tile.Palette * 16u;
                    }
                    else
                    {
                        uint tmemOffset = ((tbase << 3) + sUnsigned) & 0xFFFu;
                        index = _rdpTmem[tmemOffset];
                    }

                    if (_rdpOtherModesEnableTlut)
                        rgba = RdpTlutColorToRgba(LookupRdpTlut(index));
                    else if (index < _rdpTlut.Length && _rdpTlut[index] != 0)
                        rgba = Rgba5551ToRgba8888(_rdpTlut[index]);
                    else
                        rgba = (index << 24) | (index << 16) | (index << 8) | 0xFFu;
                    return true;
                }
                case 3u: // IA
                    return DecodeRdpIaTexture(tile, tbase, s, out rgba);
                case 4u: // I
                    return DecodeRdpIntensityTexture(tile, tbase, s, out rgba);
                default:
                    return false;
            }
        }

        private ushort LookupRdpTlut(uint index)
        {
            if (index < _rdpTlut.Length && _rdpTlut[index] != 0)
                return _rdpTlut[index];

            uint tmemAddress = 0x800u + (index & 0xFFu) * 8u;
            if (tmemAddress + 1u < _rdpTmem.Length)
                return ReadRdpTmemWord(tmemAddress);

            return 0;
        }

        private uint RdpTlutColorToRgba(ushort color)
        {
            if (!_rdpOtherModesTlutType)
                return Rgba5551ToRgba8888(color);

            uint k = ((uint)color >> 8) & 0xFFu;
            uint alpha = (uint)color & 0xFFu;
            return (k << 24) | (k << 16) | (k << 8) | alpha;
        }

        private ushort ReadRdpTmemWord(uint address)
        {
            address &= 0xFFFu;
            if (address + 1u >= _rdpTmem.Length)
                return 0;
            return (ushort)((_rdpTmem[address] << 8) | _rdpTmem[address + 1u]);
        }

        private bool DecodeRdpIaTexture(RdpTileState tile, uint tbase, int s, out uint rgba)
        {
            rgba = 0;
            uint sUnsigned = (uint)Math.Max(0, s);
            switch (tile.Size & 0x3u)
            {
                case 0u:
                {
                    uint tmemOffset = ((tbase << 3) + (sUnsigned >> 1)) & 0xFFFu;
                    byte packed = _rdpTmem[tmemOffset];
                    uint value = (sUnsigned & 1u) == 0u ? (uint)(packed >> 4) : (uint)(packed & 0x0F);
                    uint intensity = ((value >> 1) & 0x7u) * 255u / 7u;
                    uint alpha = (value & 1u) != 0 ? 0xFFu : 0u;
                    rgba = (intensity << 24) | (intensity << 16) | (intensity << 8) | alpha;
                    return true;
                }
                case 1u:
                {
                    uint tmemOffset = ((tbase << 3) + sUnsigned) & 0xFFFu;
                    uint value = _rdpTmem[tmemOffset];
                    uint intensity = ((value >> 4) & 0xFu) * 17u;
                    uint alpha = (value & 0xFu) * 17u;
                    rgba = (intensity << 24) | (intensity << 16) | (intensity << 8) | alpha;
                    return true;
                }
                case 2u:
                {
                    uint tmemOffset = ((tbase << 3) + sUnsigned * 2u) & 0xFFFu;
                    if (tmemOffset + 1u >= _rdpTmem.Length)
                        return false;
                    rgba = ((uint)_rdpTmem[tmemOffset] << 24)
                        | ((uint)_rdpTmem[tmemOffset] << 16)
                        | ((uint)_rdpTmem[tmemOffset] << 8)
                        | _rdpTmem[tmemOffset + 1u];
                    return true;
                }
                default:
                    return false;
            }
        }

        private bool DecodeRdpIntensityTexture(RdpTileState tile, uint tbase, int s, out uint rgba)
        {
            rgba = 0;
            uint sUnsigned = (uint)Math.Max(0, s);
            uint intensity;
            if ((tile.Size & 0x3u) == 0u)
            {
                uint tmemOffset = ((tbase << 3) + (sUnsigned >> 1)) & 0xFFFu;
                byte packed = _rdpTmem[tmemOffset];
                intensity = ((sUnsigned & 1u) == 0u ? (uint)(packed >> 4) : (uint)(packed & 0x0F)) * 17u;
            }
            else
            {
                uint tmemOffset = ((tbase << 3) + sUnsigned) & 0xFFFu;
                intensity = _rdpTmem[tmemOffset];
            }

            rgba = (intensity << 24) | (intensity << 16) | (intensity << 8) | 0xFFu;
            return true;
        }

        private void DrawRdpSolidRectangle(uint x0, uint y0, uint x1, uint y1, uint rgba, uint bytesPerPixel)
        {
            if (!ClampRdpRectangleToScissor(ref x0, ref y0, ref x1, ref y1))
                return;
            if (x0 >= _rdpColorImageWidth)
                return;
            if (x1 >= _rdpColorImageWidth)
                x1 = _rdpColorImageWidth - 1u;

            uint maxRows = (uint)((RDRAM.Length - _rdpColorImageAddress) / (_rdpColorImageWidth * bytesPerPixel));
            if (maxRows == 0 || y0 >= maxRows)
                return;
            if (y1 >= maxRows)
                y1 = maxRows - 1u;

            for (uint y = y0; y <= y1; y++)
            {
                uint rowStart = _rdpColorImageAddress + ((y * _rdpColorImageWidth + x0) * bytesPerPixel);
                uint rowPixels = x1 - x0 + 1u;
                for (uint x = 0; x < rowPixels; x++)
                {
                    uint address = _rdpColorImageAddress + ((y * _rdpColorImageWidth + x0 + x) * bytesPerPixel);
                    WriteRdpRgbaPixel(address, rgba, bytesPerPixel);
                }
                NoteRdramWriteRange(rowStart, rowPixels * bytesPerPixel);
            }

            long pixels = (long)(y1 - y0 + 1u) * (x1 - x0 + 1u);
            NoteRdpPixelWrites(pixels, IsRgbaNonZero(rgba) ? pixels : 0);
            MarkRdpColorImageWritten(bytesPerPixel);
        }

        private void NoteRdpPixelWrites(long totalPixels, long nonZeroPixels)
        {
            if (totalPixels <= 0)
                return;

            Interlocked.Add(ref _rdpPixelWriteCount, totalPixels);
            if (nonZeroPixels > 0)
                Interlocked.Add(ref _rdpNonZeroPixelWriteCount, nonZeroPixels);
        }

        private static bool IsRgbaNonZero(uint rgba)
        {
            return (rgba & 0xFFFFFF00u) != 0;
        }

        private bool ShouldRejectRdpAlpha(uint rgba)
        {
            return _rdpOtherModesAlphaCompare && (rgba & 0xFFu) == 0u;
        }

        private bool IsRdpFillColorRgbNonZero(uint bytesPerPixel)
        {
            switch (bytesPerPixel)
            {
                case 1u:
                    return (_rdpFillColor & 0xFFFFFFFFu) != 0;
                case 2u:
                    return ((_rdpFillColor >> 16) & 0xFFFEu) != 0
                        || (_rdpFillColor & 0xFFFEu) != 0;
                case 4u:
                    return (_rdpFillColor & 0xFFFFFF00u) != 0;
                default:
                    return false;
            }
        }

        private void MarkRdpColorImageWritten(uint bytesPerPixel)
        {
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0)
                return;

            Volatile.Write(ref _lastRdpColorImageAddress, _rdpColorImageAddress);
            Volatile.Write(ref _lastRdpColorImageWidth, _rdpColorImageWidth);
            Volatile.Write(ref _lastRdpColorImageBytesPerPixel, bytesPerPixel);
            Volatile.Write(ref _lastRdpColorImageWriteEpoch, _rdramWriteEpoch);
        }

        private void CaptureVisibleRdpFramebufferSnapshot(uint bytesPerPixel, long knownVisiblePixels = 0)
        {
            long perfStart = StartPerfTimer();
            try
            {
            int snapshotLength = 0;
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0)
                return;

            uint visibleHeight = GetFramebufferHeightHint();
            if (visibleHeight == 0)
                visibleHeight = 240u;

            ulong rowBytes = (ulong)_rdpColorImageWidth * bytesPerPixel;
            if (rowBytes == 0)
                return;

            uint maxHeight = (uint)Math.Min(480UL, ((ulong)RDRAM.Length - _rdpColorImageAddress) / rowBytes);
            uint snapshotHeight = Math.Min(maxHeight, visibleHeight + 8u);
            ulong length64 = rowBytes * snapshotHeight;
            if (length64 == 0 || length64 > int.MaxValue || (ulong)_rdpColorImageAddress + length64 > (ulong)RDRAM.Length)
                return;

            if (knownVisiblePixels < 512 && !HasRdpVisibleFramebufferContent(bytesPerPixel, 512u))
                return;

            lock (_lastVisibleRdpFramebufferLock)
            {
                int slot = SelectVisibleRdpFramebufferSnapshotSlotLocked(
                    _rdpColorImageAddress,
                    _rdpColorImageWidth,
                    bytesPerPixel);
                byte[] snapshot = _visibleRdpFramebufferSnapshots[slot];
                if (snapshot == null || snapshot.Length != (int)length64)
                    snapshot = new byte[(int)length64];

                Buffer.BlockCopy(RDRAM, (int)_rdpColorImageAddress, snapshot, 0, snapshot.Length);
                snapshotLength = snapshot.Length;

                _lastVisibleRdpFramebufferSnapshot = snapshot;
                _lastVisibleRdpFramebufferAddress = _rdpColorImageAddress;
                _lastVisibleRdpFramebufferWidth = _rdpColorImageWidth;
                _lastVisibleRdpFramebufferHeight = snapshotHeight;
                _lastVisibleRdpFramebufferBytesPerPixel = bytesPerPixel;
                _lastVisibleRdpFramebufferEpoch = _rdramWriteEpoch;
                StoreVisibleRdpFramebufferSnapshotLocked(
                    slot,
                    snapshot,
                    _rdpColorImageAddress,
                    _rdpColorImageWidth,
                    snapshotHeight,
                    bytesPerPixel,
                    _rdramWriteEpoch);
            }
            if (EnableN64Perf)
                Interlocked.Add(ref _perfRdpSnapshotBytes, snapshotLength);
            }
            finally
            {
                AddPerfTicks(ref _perfRdpSnapshotTicks, ref _perfRdpSnapshotCalls, perfStart);
            }
        }

        private uint CountRdpVisibleFramebufferContent(uint bytesPerPixel, uint height)
        {
            long perfStart = StartPerfTimer();
            ulong perfPixels = 0;
            try
            {
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0
                || height == 0)
                return 0;

            ulong pixels64 = (ulong)_rdpColorImageWidth * height;
            ulong length64 = pixels64 * bytesPerPixel;
            if (pixels64 == 0 || length64 > int.MaxValue || (ulong)_rdpColorImageAddress + length64 > (ulong)RDRAM.Length)
                return 0;

            perfPixels = pixels64;
            uint visible = 0;
            uint address = _rdpColorImageAddress;
            for (ulong i = 0; i < pixels64; i++, address += bytesPerPixel)
            {
                bool rgbNonZero;
                if (bytesPerPixel == 2u)
                {
                    ushort value = (ushort)((RDRAM[address] << 8) | RDRAM[address + 1u]);
                    rgbNonZero = (value & 0xFFFEu) != 0;
                }
                else if (bytesPerPixel == 4u)
                {
                    rgbNonZero = RDRAM[address] != 0 || RDRAM[address + 1u] != 0 || RDRAM[address + 2u] != 0;
                }
                else
                {
                    rgbNonZero = RDRAM[address] != 0;
                }

                if (rgbNonZero)
                    visible++;
            }

            return visible;
            }
            finally
            {
                if (EnableN64Perf && perfPixels > 0)
                    Interlocked.Add(ref _perfRdpVisibleScanPixels, perfPixels > long.MaxValue ? long.MaxValue : (long)perfPixels);
                AddPerfTicks(ref _perfRdpVisibleScanTicks, ref _perfRdpVisibleScanCalls, perfStart);
            }
        }

        private bool HasRdpVisibleFramebufferContent(uint bytesPerPixel, uint minVisiblePixels)
        {
            long perfStart = StartPerfTimer();
            ulong perfPixels = 0;
            try
            {
            if (_rdpColorImageAddress < PlausibleFramebufferOriginFloor
                || _rdpColorImageAddress >= RDRAM.Length
                || _rdpColorImageWidth == 0
                || bytesPerPixel == 0)
                return false;

            uint height = GetFramebufferHeightHint();
            if (height == 0)
                height = 240u;

            ulong pixels64 = (ulong)_rdpColorImageWidth * height;
            ulong length64 = pixels64 * bytesPerPixel;
            if (pixels64 == 0 || length64 > int.MaxValue || (ulong)_rdpColorImageAddress + length64 > (ulong)RDRAM.Length)
                return false;

            perfPixels = pixels64;
            uint visible = 0;
            uint address = _rdpColorImageAddress;
            for (ulong i = 0; i < pixels64; i++, address += bytesPerPixel)
            {
                bool rgbNonZero;
                if (bytesPerPixel == 2u)
                {
                    ushort value = (ushort)((RDRAM[address] << 8) | RDRAM[address + 1u]);
                    rgbNonZero = (value & 0xFFFEu) != 0;
                }
                else if (bytesPerPixel == 4u)
                {
                    rgbNonZero = RDRAM[address] != 0 || RDRAM[address + 1u] != 0 || RDRAM[address + 2u] != 0;
                }
                else
                {
                    rgbNonZero = RDRAM[address] != 0;
                }

                if (rgbNonZero && ++visible >= minVisiblePixels)
                    return true;
            }

            return false;
            }
            finally
            {
                if (EnableN64Perf && perfPixels > 0)
                    Interlocked.Add(ref _perfRdpVisibleScanPixels, perfPixels > long.MaxValue ? long.MaxValue : (long)perfPixels);
                AddPerfTicks(ref _perfRdpVisibleScanTicks, ref _perfRdpVisibleScanCalls, perfStart);
            }
        }

        public bool TryCopyLastVisibleRdpFramebufferSnapshot(
            uint requestedAddress,
            uint width,
            uint height,
            uint bytesPerPixel,
            out byte[] snapshot,
            out uint address,
            out uint epoch)
        {
            snapshot = Array.Empty<byte>();
            address = 0;
            epoch = 0;

            if (width == 0 || height == 0 || bytesPerPixel == 0)
                return false;

            ulong requested64 = (ulong)width * height * bytesPerPixel;
            if (requested64 == 0 || requested64 > int.MaxValue)
                return false;

            int requested = (int)requested64;
            ulong rowBytes = (ulong)width * bytesPerPixel;
            lock (_lastVisibleRdpFramebufferLock)
            {
                int bestSlot = -1;
                uint bestEpoch = 0;
                ulong bestOffset = 0;
                for (int i = 0; i < RdpVisibleFramebufferSnapshotSlots; i++)
                {
                    byte[] candidate = _visibleRdpFramebufferSnapshots[i];
                    uint candidateAddress = _visibleRdpFramebufferAddresses[i];
                    ulong offsetBytes = 0;
                    if (candidateAddress == requestedAddress)
                    {
                        offsetBytes = 0;
                    }
                    else if (requestedAddress > candidateAddress)
                    {
                        offsetBytes = requestedAddress - candidateAddress;
                        if (rowBytes == 0
                            || offsetBytes > rowBytes * 8UL
                            || offsetBytes % bytesPerPixel != 0)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }

                    if (candidate == null
                        || (ulong)candidate.Length < offsetBytes + requested64
                        || _visibleRdpFramebufferWidths[i] != width
                        || _visibleRdpFramebufferHeights[i] < height
                        || _visibleRdpFramebufferBytesPerPixels[i] != bytesPerPixel
                        || _visibleRdpFramebufferEpochs[i] == 0)
                    {
                        continue;
                    }

                    if (bestSlot < 0 || IsEpochNewer(_visibleRdpFramebufferEpochs[i], bestEpoch))
                    {
                        bestSlot = i;
                        bestEpoch = _visibleRdpFramebufferEpochs[i];
                        bestOffset = offsetBytes;
                    }
                }

                if (bestSlot < 0)
                    return false;

                snapshot = new byte[requested];
                Buffer.BlockCopy(_visibleRdpFramebufferSnapshots[bestSlot], (int)bestOffset, snapshot, 0, requested);
                address = requestedAddress;
                epoch = _visibleRdpFramebufferEpochs[bestSlot];
                return true;
            }
        }

        public bool TryCopyLastVisibleRdpFramebufferSnapshot(
            uint requestedAddress,
            uint width,
            uint height,
            uint bytesPerPixel,
            byte[] destination,
            out uint address,
            out uint epoch)
        {
            address = 0;
            epoch = 0;

            if (width == 0 || height == 0 || bytesPerPixel == 0 || destination == null)
                return false;

            ulong requested64 = (ulong)width * height * bytesPerPixel;
            if (requested64 == 0 || requested64 > int.MaxValue || destination.Length < (int)requested64)
                return false;

            int requested = (int)requested64;
            ulong rowBytes = (ulong)width * bytesPerPixel;
            lock (_lastVisibleRdpFramebufferLock)
            {
                int bestSlot = -1;
                uint bestEpoch = 0;
                ulong bestOffset = 0;
                for (int i = 0; i < RdpVisibleFramebufferSnapshotSlots; i++)
                {
                    byte[] candidate = _visibleRdpFramebufferSnapshots[i];
                    uint candidateAddress = _visibleRdpFramebufferAddresses[i];
                    ulong offsetBytes = 0;
                    if (candidateAddress == requestedAddress)
                    {
                        offsetBytes = 0;
                    }
                    else if (requestedAddress > candidateAddress)
                    {
                        offsetBytes = requestedAddress - candidateAddress;
                        if (rowBytes == 0
                            || offsetBytes > rowBytes * 8UL
                            || offsetBytes % bytesPerPixel != 0)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }

                    if (candidate == null
                        || (ulong)candidate.Length < offsetBytes + requested64
                        || _visibleRdpFramebufferWidths[i] != width
                        || _visibleRdpFramebufferHeights[i] < height
                        || _visibleRdpFramebufferBytesPerPixels[i] != bytesPerPixel
                        || _visibleRdpFramebufferEpochs[i] == 0)
                    {
                        continue;
                    }

                    if (bestSlot < 0 || IsEpochNewer(_visibleRdpFramebufferEpochs[i], bestEpoch))
                    {
                        bestSlot = i;
                        bestEpoch = _visibleRdpFramebufferEpochs[i];
                        bestOffset = offsetBytes;
                    }
                }

                if (bestSlot < 0)
                    return false;

                Buffer.BlockCopy(_visibleRdpFramebufferSnapshots[bestSlot], (int)bestOffset, destination, 0, requested);
                address = requestedAddress;
                epoch = _visibleRdpFramebufferEpochs[bestSlot];
                return true;
            }
        }

        public bool TryCopyBestVisibleRdpFramebufferSnapshot(
            uint width,
            uint height,
            uint bytesPerPixel,
            out byte[] snapshot,
            out uint address,
            out uint epoch)
        {
            snapshot = Array.Empty<byte>();
            address = 0;
            epoch = 0;

            if (width == 0 || height == 0 || bytesPerPixel == 0)
                return false;

            ulong requested64 = (ulong)width * height * bytesPerPixel;
            if (requested64 == 0 || requested64 > int.MaxValue)
                return false;

            int requested = (int)requested64;
            lock (_lastVisibleRdpFramebufferLock)
            {
                int bestSlot = -1;
                uint bestEpoch = 0;
                for (int i = 0; i < RdpVisibleFramebufferSnapshotSlots; i++)
                {
                    byte[] candidate = _visibleRdpFramebufferSnapshots[i];
                    if (candidate == null
                        || candidate.Length < requested
                        || _visibleRdpFramebufferWidths[i] != width
                        || _visibleRdpFramebufferHeights[i] < height
                        || _visibleRdpFramebufferBytesPerPixels[i] != bytesPerPixel
                        || _visibleRdpFramebufferEpochs[i] == 0)
                    {
                        continue;
                    }

                    if (bestSlot < 0 || IsEpochNewer(_visibleRdpFramebufferEpochs[i], bestEpoch))
                    {
                        bestSlot = i;
                        bestEpoch = _visibleRdpFramebufferEpochs[i];
                    }
                }

                if (bestSlot < 0)
                    return false;

                snapshot = new byte[requested];
                Buffer.BlockCopy(_visibleRdpFramebufferSnapshots[bestSlot], 0, snapshot, 0, requested);
                address = _visibleRdpFramebufferAddresses[bestSlot];
                epoch = _visibleRdpFramebufferEpochs[bestSlot];
                return true;
            }
        }

        public bool TryCopyBestVisibleRdpFramebufferSnapshot(
            uint width,
            uint height,
            uint bytesPerPixel,
            byte[] destination,
            out uint address,
            out uint epoch)
        {
            address = 0;
            epoch = 0;

            if (width == 0 || height == 0 || bytesPerPixel == 0 || destination == null)
                return false;

            ulong requested64 = (ulong)width * height * bytesPerPixel;
            if (requested64 == 0 || requested64 > int.MaxValue || destination.Length < (int)requested64)
                return false;

            int requested = (int)requested64;
            lock (_lastVisibleRdpFramebufferLock)
            {
                int bestSlot = -1;
                uint bestEpoch = 0;
                for (int i = 0; i < RdpVisibleFramebufferSnapshotSlots; i++)
                {
                    byte[] candidate = _visibleRdpFramebufferSnapshots[i];
                    if (candidate == null
                        || candidate.Length < requested
                        || _visibleRdpFramebufferWidths[i] != width
                        || _visibleRdpFramebufferHeights[i] < height
                        || _visibleRdpFramebufferBytesPerPixels[i] != bytesPerPixel
                        || _visibleRdpFramebufferEpochs[i] == 0)
                    {
                        continue;
                    }

                    if (bestSlot < 0 || IsEpochNewer(_visibleRdpFramebufferEpochs[i], bestEpoch))
                    {
                        bestSlot = i;
                        bestEpoch = _visibleRdpFramebufferEpochs[i];
                    }
                }

                if (bestSlot < 0)
                    return false;

                Buffer.BlockCopy(_visibleRdpFramebufferSnapshots[bestSlot], 0, destination, 0, requested);
                address = _visibleRdpFramebufferAddresses[bestSlot];
                epoch = _visibleRdpFramebufferEpochs[bestSlot];
                return true;
            }
        }

        private void StoreVisibleRdpFramebufferSnapshotLocked(
            byte[] snapshot,
            uint address,
            uint width,
            uint height,
            uint bytesPerPixel,
            uint epoch)
        {
            int slot = SelectVisibleRdpFramebufferSnapshotSlotLocked(address, width, bytesPerPixel);

            StoreVisibleRdpFramebufferSnapshotLocked(slot, snapshot, address, width, height, bytesPerPixel, epoch);
        }

        private void StoreVisibleRdpFramebufferSnapshotLocked(
            int slot,
            byte[] snapshot,
            uint address,
            uint width,
            uint height,
            uint bytesPerPixel,
            uint epoch)
        {
            _visibleRdpFramebufferSnapshots[slot] = snapshot;
            _visibleRdpFramebufferAddresses[slot] = address;
            _visibleRdpFramebufferWidths[slot] = width;
            _visibleRdpFramebufferHeights[slot] = height;
            _visibleRdpFramebufferBytesPerPixels[slot] = bytesPerPixel;
            _visibleRdpFramebufferEpochs[slot] = epoch;
        }

        private int SelectVisibleRdpFramebufferSnapshotSlotLocked(uint address, uint width, uint bytesPerPixel)
        {
            for (int i = 0; i < RdpVisibleFramebufferSnapshotSlots; i++)
            {
                if (_visibleRdpFramebufferAddresses[i] == address
                    && _visibleRdpFramebufferWidths[i] == width
                    && _visibleRdpFramebufferBytesPerPixels[i] == bytesPerPixel)
                {
                    return i;
                }
            }

            int slot = _visibleRdpFramebufferSnapshotCursor;
            _visibleRdpFramebufferSnapshotCursor = (_visibleRdpFramebufferSnapshotCursor + 1) % RdpVisibleFramebufferSnapshotSlots;
            return slot;
        }

        private void ResetVisibleRdpFramebufferSnapshotCacheLocked()
        {
            for (int i = 0; i < RdpVisibleFramebufferSnapshotSlots; i++)
            {
                _visibleRdpFramebufferSnapshots[i] = null;
                _visibleRdpFramebufferAddresses[i] = 0;
                _visibleRdpFramebufferWidths[i] = 0;
                _visibleRdpFramebufferHeights[i] = 0;
                _visibleRdpFramebufferBytesPerPixels[i] = 0;
                _visibleRdpFramebufferEpochs[i] = 0;
            }

            _visibleRdpFramebufferSnapshotCursor = 0;
        }

        private static bool IsEpochNewer(uint candidate, uint current)
        {
            return current == 0 || unchecked(candidate - current) < 0x80000000u;
        }

        private uint SelectRdpSolidColor()
        {
            uint color = _rdpPrimColor;
            if ((color & 0xFFFFFF00u) == 0)
                color = _rdpEnvColor;
            if ((color & 0xFFFFFF00u) == 0)
                color = _rdpBlendColor;
            return color;
        }

        private void WriteRdpFillPixel(uint address, uint pixelIndex, uint bytesPerPixel)
        {
            if (address >= RDRAM.Length)
                return;

            switch (bytesPerPixel)
            {
                case 1u:
                    RDRAM[address] = (byte)((_rdpFillColor >> (int)((3u - (address & 3u)) * 8u)) & 0xFFu);
                    if ((address & 1u) != 0)
                    {
                        uint hiddenIndex8 = address >> 1;
                        if (hiddenIndex8 < _rdpHiddenBits.Length)
                            _rdpHiddenBits[hiddenIndex8] = (byte)(((RDRAM[address] & 1u) << 1) | (RDRAM[address] & 1u));
                    }
                    break;
                case 2u:
                    if (address + 1u >= RDRAM.Length)
                        return;
                    ushort color16 = (pixelIndex & 1u) == 0u
                        ? (ushort)(_rdpFillColor >> 16)
                        : (ushort)_rdpFillColor;
                    RDRAM[address] = (byte)(color16 >> 8);
                    RDRAM[address + 1u] = (byte)color16;
                    uint hiddenIndex16 = address >> 1;
                    if (hiddenIndex16 < _rdpHiddenBits.Length)
                        _rdpHiddenBits[hiddenIndex16] = (byte)(((color16 & 1u) << 1) | (color16 & 1u));
                    break;
                case 4u:
                    if (address + 3u >= RDRAM.Length)
                        return;
                    RDRAM[address] = (byte)(_rdpFillColor >> 24);
                    RDRAM[address + 1u] = (byte)(_rdpFillColor >> 16);
                    RDRAM[address + 2u] = (byte)(_rdpFillColor >> 8);
                    RDRAM[address + 3u] = (byte)_rdpFillColor;
                    uint hiddenIndex32 = address >> 1;
                    if (hiddenIndex32 + 1u < _rdpHiddenBits.Length)
                    {
                        _rdpHiddenBits[hiddenIndex32] = (_rdpFillColor & 0x10000u) != 0 ? (byte)3 : (byte)0;
                        _rdpHiddenBits[hiddenIndex32 + 1u] = (_rdpFillColor & 1u) != 0 ? (byte)3 : (byte)0;
                    }
                    break;
            }
        }

        private void WriteRdpRgbaPixel(uint address, uint rgba, uint bytesPerPixel)
        {
            if (address >= RDRAM.Length)
                return;

            if (ShouldBlendRdpPixel(rgba) && !TryBlendRdpPixel(address, ref rgba, bytesPerPixel))
                return;

            switch (bytesPerPixel)
            {
                case 1u:
                    RDRAM[address] = (byte)((((rgba >> 24) & 0xFFu) + ((rgba >> 16) & 0xFFu) + ((rgba >> 8) & 0xFFu)) / 3u);
                    break;
                case 2u:
                    if (address + 1u >= RDRAM.Length)
                        return;
                    ushort color16 = ApplyRdpColorCoverage(Rgba8888ToRgba5551(rgba));
                    RDRAM[address] = (byte)(color16 >> 8);
                    RDRAM[address + 1u] = (byte)color16;
                    StoreRdpColorCoverage(address, color16);
                    break;
                case 4u:
                    if (address + 3u >= RDRAM.Length)
                        return;
                    RDRAM[address] = (byte)(rgba >> 24);
                    RDRAM[address + 1u] = (byte)(rgba >> 16);
                    RDRAM[address + 2u] = (byte)(rgba >> 8);
                    RDRAM[address + 3u] = (byte)rgba;
                    break;
            }
        }

        private ushort ApplyRdpColorCoverage(ushort color16)
        {
            return _rdpOtherModesCvgDest == 3u ? color16 : (ushort)(color16 | 1u);
        }

        private void StoreRdpColorCoverage(uint address, ushort color16)
        {
            uint hiddenIndex = address >> 1;
            if (hiddenIndex >= _rdpHiddenBits.Length)
                return;

            if (_rdpOtherModesCvgDest == 3u)
                return;

            _rdpHiddenBits[hiddenIndex] = (byte)((color16 & 1u) != 0 ? 3u : 0u);
        }

        private bool ShouldBlendRdpPixel(uint rgba)
        {
            return _rdpOtherModesForceBlend && (_rdpOtherModesImageRead || (rgba & 0xFFu) < 0xFFu);
        }

        private bool TryBlendRdpPixel(uint address, ref uint rgba, uint bytesPerPixel)
        {
            uint alpha = rgba & 0xFFu;
            if (alpha == 0u)
                return false;
            if (alpha >= 0xFFu)
                return true;

            uint dst;
            switch (bytesPerPixel)
            {
                case 1u:
                    {
                        uint y = RDRAM[address];
                        dst = (y << 24) | (y << 16) | (y << 8) | 0xFFu;
                        break;
                    }
                case 2u:
                    {
                        if (address + 1u >= RDRAM.Length)
                            return false;
                        ushort color16 = (ushort)((RDRAM[address] << 8) | RDRAM[address + 1u]);
                        dst = Rgba5551ToRgba8888(color16) | 0xFFu;
                        break;
                    }
                case 4u:
                    {
                        if (address + 3u >= RDRAM.Length)
                            return false;
                        dst = ((uint)RDRAM[address] << 24)
                            | ((uint)RDRAM[address + 1u] << 16)
                            | ((uint)RDRAM[address + 2u] << 8)
                            | 0xFFu;
                        break;
                    }
                default:
                    return false;
            }

            rgba = AlphaBlendRdpRgba(rgba, dst, alpha);
            return true;
        }

        private static uint AlphaBlendRdpRgba(uint src, uint dst, uint alpha)
        {
            uint invAlpha = 0xFFu - alpha;
            uint r = BlendRdpChannel((src >> 24) & 0xFFu, (dst >> 24) & 0xFFu, alpha, invAlpha);
            uint g = BlendRdpChannel((src >> 16) & 0xFFu, (dst >> 16) & 0xFFu, alpha, invAlpha);
            uint b = BlendRdpChannel((src >> 8) & 0xFFu, (dst >> 8) & 0xFFu, alpha, invAlpha);
            return (r << 24) | (g << 16) | (b << 8) | 0xFFu;
        }

        private static uint BlendRdpChannel(uint src, uint dst, uint alpha, uint invAlpha)
        {
            return ((src * alpha) + (dst * invAlpha) + 0x7Fu) / 0xFFu;
        }

        private static ushort Rgba8888ToRgba5551(uint rgba)
        {
            uint r = (rgba >> 24) & 0xFFu;
            uint g = (rgba >> 16) & 0xFFu;
            uint b = (rgba >> 8) & 0xFFu;
            uint a = rgba & 0xFFu;
            return (ushort)((QuantizeRgbaChannelTo5551(r) << 11)
                | (QuantizeRgbaChannelTo5551(g) << 6)
                | (QuantizeRgbaChannelTo5551(b) << 1)
                | (a >= 0x80u ? 1u : 0u));
        }

        private static uint QuantizeRgbaChannelTo5551(uint value)
        {
            if (value == 0)
                return 0;

            uint quantized = value >> 3;
            return quantized == 0 ? 1u : quantized;
        }

        private static uint Rgba5551ToRgba8888(ushort color)
        {
            uint r = (uint)((color >> 11) & 0x1F);
            uint g = (uint)((color >> 6) & 0x1F);
            uint b = (uint)((color >> 1) & 0x1F);
            uint a = (color & 1u) != 0 ? 0xFFu : 0u;
            r = (r << 3) | (r >> 2);
            g = (g << 3) | (g >> 2);
            b = (b << 3) | (b >> 2);
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        private void PostFramebufferWrite(uint address, uint length, uint epoch)
        {
            if (length == 0 || _fbInfos[0].Addr == 0)
                return;

            uint begin = address & 0x1FFFFFFFu;
            uint end = begin + length - 1u;
            if (end >= RDRAM.Length)
                end = (uint)RDRAM.Length - 1u;

            for (int i = 0; i < _fbInfos.Length; i++)
            {
                ref TrackedFramebufferInfo info = ref _fbInfos[i];
                uint bufferSize = GetFramebufferBufferSize(info);
                if (bufferSize == 0)
                    continue;

                uint fbBegin = info.Addr;
                uint fbEnd = fbBegin + bufferSize - 1u;
                if (begin > fbEnd || end < fbBegin)
                    continue;

                info.WriteEpoch = epoch;
                uint overlapBegin = begin > fbBegin ? begin : fbBegin;
                uint overlapEnd = end < fbEnd ? end : fbEnd;
                MarkFramebufferPagesDirty(overlapBegin, overlapEnd - overlapBegin + 1u);
            }
        }

        public void NotifyFramebufferConsumerRead(uint address, uint length)
        {
            if (length == 0 || _fbInfos[0].Addr == 0)
                return;

            uint begin = address & 0x1FFFFFFFu;
            if (begin >= RDRAM.Length)
                return;

            uint end = begin + length - 1u;
            if (end >= RDRAM.Length)
                end = (uint)RDRAM.Length - 1u;

            uint epoch = ++_fbInfoEpoch;
            for (int i = 0; i < _fbInfos.Length; i++)
            {
                ref TrackedFramebufferInfo info = ref _fbInfos[i];
                uint bufferSize = GetFramebufferBufferSize(info);
                if (bufferSize == 0)
                    continue;

                uint fbBegin = info.Addr;
                uint fbEnd = fbBegin + bufferSize - 1u;
                if (begin > fbEnd || end < fbBegin)
                    continue;

                info.LastReadEpoch = epoch;

                uint overlapBegin = begin > fbBegin ? begin : fbBegin;
                uint overlapEnd = end < fbEnd ? end : fbEnd;
                int startPage = (int)(overlapBegin / RdramPageSize);
                int endPage = (int)(overlapEnd / RdramPageSize);
                for (int page = startPage; page <= endPage; page++)
                    _fbDirtyPage[page] = 0;

                if (TraceFramebufferLifecycle)
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64FB] read slot={i} addr=0x{info.Addr:x8} read=0x{begin:x8}-0x{end:x8} " +
                        $"overlap=0x{overlapBegin:x8}-0x{overlapEnd:x8} readEpoch={epoch} pc=0x{Registers.R4300.PC:x8}");
                }
            }
        }

        public uint FindTrackedFramebufferOriginCandidate(uint width, uint height, uint bytesPerPixel, uint viOrigin, out ulong bestScore, out uint bestDirtyPages)
        {
            bestScore = 0;
            bestDirtyPages = 0;

            uint bestOrigin = viOrigin;
            for (int i = 0; i < _fbInfos.Length; i++)
            {
                TrackedFramebufferInfo info = _fbInfos[i];
                uint bufferSize = GetFramebufferBufferSize(info);
                if (bufferSize == 0)
                    continue;
                if (info.Size != bytesPerPixel)
                    continue;
                if (width != 0 && info.Width != 0 && Math.Abs((int)info.Width - (int)width) > 16)
                    continue;
                if (height != 0 && info.Height != 0 && Math.Abs((int)info.Height - (int)height) > 48)
                    continue;

                uint fbBegin = info.Addr;
                uint fbEnd = fbBegin + bufferSize - 1u;
                uint dirtyPages = 0;
                int startPage = (int)(fbBegin / RdramPageSize);
                int endPage = (int)(fbEnd / RdramPageSize);
                for (int page = startPage; page <= endPage && page < _fbDirtyPage.Length; page++)
                {
                    if (_fbDirtyPage[page] != 0)
                        dirtyPages++;
                }

                ulong score = ((ulong)dirtyPages << 48)
                    | ((ulong)info.WriteEpoch << 16)
                    | info.SetEpoch;

                if (info.LastReadEpoch != 0 && info.WriteEpoch <= info.LastReadEpoch)
                    score >>= 4;

                if (viOrigin >= fbBegin && viOrigin <= fbEnd)
                    score |= 1UL << 60;

                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestDirtyPages = dirtyPages;
                bestOrigin = info.Addr;
            }

            return bestOrigin;
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
            // VI_INTR gates vertical interrupts, but the interrupt event itself is
            // frame-paced. Scheduling directly to VI_INTR line makes the first OoT
            // VI fire almost immediately after register setup.
            if (viIntrLine >= viVSync)
            {
                _viInterruptCyclesRemaining = 0;
                return;
            }

            _viInterruptCyclesRemaining = _viFrameDelayCycles;
        }

        public Memory(byte[] Rom)
        {
            _rom = Rom;
            _rspInterpreter = new RspInterpreter(this);
            for (int i = 0; i < _rdpHiddenBits.Length; i++)
                _rdpHiddenBits[i] = 3;

            // RDRAM (base + expansion/mirror window).
            // Keep backing array at 8 MiB for now; accesses beyond that window mirror via ResolveArrayOffset().
            MemoryMapList.Add(new MemEntry(0x00000000, 0x03EFFFFF, RDRAM, RDRAM,       "RDRAM"));
            MemoryMapList.Add(new MemEntry(0x03F00000, 0x03FFFFFF, RDRAMReg, RDRAMReg, "RDRAM Registers"));

            // SP Registers
            MemoryMapList.Add(new MemEntry(0x04000000, 0x04000FFF, SP_MEM_RW,          SP_MEM_RW,           "SP_DMEM",
                null, null, 0x0000, 0x0000));
            MemoryMapList.Add(new MemEntry(0x04001000, 0x04001FFF, SP_MEM_RW,          SP_MEM_RW,           "SP_IMEM",
                null, null, 0x1000, 0x1000));
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

        private void ArmPiDmaCompletion(uint transferBytes, uint minimumDelayCycles)
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
            _piInterruptDelayRemaining = Math.Max(minimumDelayCycles, transferBytes / 8u);
            if (_piInterruptDelayRemaining == 0)
                _piInterruptDelayRemaining = 1;
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

        private static bool IsCartridgeBusPhysicalAddress(uint physical)
        {
            return (physical >= 0x05000000u && physical <= 0x1FBFFFFFu)
                || (physical >= 0x1FC00800u && physical <= 0x1FFFFFFFu);
        }

        private byte ReadCartridgeBusByte(uint physical)
        {
            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            if ((piStatus & PiStatusIoBusy) != 0)
            {
                int shift = (int)((3u - (physical & 0x3u)) * 8u);
                return (byte)((_cartridgeBusLastWriteWord >> shift) & 0xFFu);
            }

            MemEntry entry = GetEntry(physical);
            if (entry.ReadArray == null)
                return 0;

            uint regOffset = physical - entry.StartAddress;
            int offset = ResolveArrayOffset(entry.ReadArray, regOffset, entry.ReadBaseOffset);
            return entry.ReadArray[offset];
        }

        private ushort ReadCartridgeBusUInt16(uint physical)
        {
            return (ushort)((ReadCartridgeBusByte(physical) << 8) | ReadCartridgeBusByte(physical + 1u));
        }

        private uint ReadCartridgeBusUInt32(uint physical)
        {
            return ((uint)ReadCartridgeBusByte(physical) << 24)
                | ((uint)ReadCartridgeBusByte(physical + 1u) << 16)
                | ((uint)ReadCartridgeBusByte(physical + 2u) << 8)
                | ReadCartridgeBusByte(physical + 3u);
        }

        private void UpdateCartridgeBusLastWrite(uint physical, uint value, int size)
        {
            switch (size)
            {
                case 1:
                {
                    int shift = (int)((3u - (physical & 0x3u)) * 8u);
                    uint mask = 0xFFu << shift;
                    _cartridgeBusLastWriteWord = (_cartridgeBusLastWriteWord & ~mask) | ((value & 0xFFu) << shift);
                    break;
                }

                case 2:
                {
                    int shift = (int)((2u - (physical & 0x2u)) * 8u);
                    uint mask = 0xFFFFu << shift;
                    _cartridgeBusLastWriteWord = (_cartridgeBusLastWriteWord & ~mask) | ((value & 0xFFFFu) << shift);
                    break;
                }

                default:
                    _cartridgeBusLastWriteWord = value;
                    break;
            }
        }

        private void ArmPiIoCompletion()
        {
            if (_piDmaBusy || _piInterruptDelayArmed)
                return;

            uint piStatus = ReadBigEndianWord(PI_STATUS_REG_R);
            piStatus |= PiStatusIoBusy;
            WriteBigEndianWord(PI_STATUS_REG_R, piStatus);

            _piInterruptDelayArmed = true;
            _piInterruptDelayRemaining = PiDmaCyclesMinimum;
        }

        private void HandleCartridgeBusWrite(uint physical, uint value, int size)
        {
            UpdateCartridgeBusLastWrite(physical, value, size);

            if (!ValidatePiRequest("cart-io", 0u, 0u, physical))
                return;

            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64PIDMA] cart-io-start pc=0x{Registers.R4300.PC:x8} " +
                    $"addr=0x{physical:x8} size={size} value=0x{value:x8} " +
                    $"piStatus=0x{ReadBigEndianWord(PI_STATUS_REG_R):x8} miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8}");
            }

            ArmPiIoCompletion();
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

        private void ArmDirectPifWriteCompletion()
        {
            if (_siDmaActive || _siDirectPifWriteActive || _siInterruptDelayArmed)
                return;

            _siDirectPifWriteActive = true;
            SetSiBusy(SiStatusDmaBusy | SiStatusIoBusy);
            _siInterruptDelayArmed = true;
            _siInterruptDelayRemaining = SiDmaDurationCycles;

            if (TraceSiInterruptLifecycle)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SIIRQ] arm-direct-pif pc=0x{Registers.R4300.PC:x8} " +
                    $"siStatus=0x{ReadBigEndianWord(SI_STATUS_REG_R):x8} pifCtl=0x{PIFRAM[63]:x2} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} delay=0x{_siInterruptDelayRemaining:x}");
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
                ProcessPifControlFlags();
            }

            _siDmaActive = false;
            _siDirectPifWriteActive = false;
            SetSiBusy(0);

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
            bool rspStoppedOnBreak = (status & (SpStatusHalt | SpStatusBroke)) != 0;
            bool taskLocked = !rspStoppedOnBreak;
            bool rspInterruptPending = (ReadBigEndianWord(MI_INTR_REG_R) & 0x00000001u) != 0;
            bool dpInterruptPending = _dpCompletionPending;
            bool scheduleRspInterrupt = rspInterruptPending
                || taskLocked
                || (rspStoppedOnBreak && (status & SpStatusIntrBreak) != 0);

            if (_activeRspTask.Type == 1 && dpInterruptPending)
            {
                FinalizeGraphicsTask();
                _dpInterruptDelayArmed = true;
                _dpInterruptDelayRemaining = 4000;
                _dpCompletionPending = true;
            }

            _rspTaskLocked = taskLocked;
            _rspInterruptDelayArmed = scheduleRspInterrupt;
            _rspInterruptDelayRemaining = scheduleRspInterrupt
                ? GetRspInterruptDelayCycles(_activeRspTask.Type)
                : 0;
            if (scheduleRspInterrupt)
                ClearMiSpInterrupt();

            status &= ~(SpStatusTaskDone | SpStatusBroke | SpStatusHalt);
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP task completed type={_activeRspTask.Type} pc=0x{Registers.R4300.PC:x8} " +
                    $"spStatus=0x{status:x8} locked={_rspTaskLocked} scheduleRspInt={scheduleRspInterrupt} " +
                    $"rspIntDelay={_rspInterruptDelayRemaining} dpPending={dpInterruptPending} dpDelay={_dpInterruptDelayRemaining} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} " +
                    $"dpcStatus=0x{ReadBigEndianWord(DPC_STATUS_REG_R):x8}");
            }
        }

        private void ArmSynchronousRspCompletion(ref uint status)
        {
            _rspTaskActive = false;
            _rspTaskCyclesRemaining = 0;

            uint postStatus = ReadBigEndianWord(SP_STATUS_REG_R);
            bool rspStoppedOnBreak = (postStatus & (SpStatusHalt | SpStatusBroke)) != 0;
            bool taskLocked = !rspStoppedOnBreak;
            bool rspInterruptPending = (ReadBigEndianWord(MI_INTR_REG_R) & 0x00000001u) != 0;
            bool dpInterruptPending = _dpCompletionPending;
            bool scheduleRspInterrupt = rspInterruptPending
                || taskLocked
                || (rspStoppedOnBreak && (postStatus & SpStatusIntrBreak) != 0);

            if (_activeRspTask.Type == 1 && dpInterruptPending)
            {
                FinalizeGraphicsTask();
                _dpInterruptDelayArmed = true;
                _dpInterruptDelayRemaining = 4000;
                _dpCompletionPending = true;
            }

            _rspTaskLocked = taskLocked;
            _rspInterruptDelayArmed = scheduleRspInterrupt;
            _rspInterruptDelayRemaining = scheduleRspInterrupt
                ? GetRspInterruptDelayCycles(_activeRspTask.Type)
                : 0;
            status = postStatus & ~(SpStatusTaskDone | SpStatusBroke | SpStatusHalt);
            if (scheduleRspInterrupt)
                ClearMiSpInterrupt();
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] ArmSynchronousRspCompletion type={_activeRspTask.Type} pc=0x{Registers.R4300.PC:x8} " +
                    $"postStatus=0x{postStatus:x8} newStatus=0x{status:x8} locked={_rspTaskLocked} " +
                    $"scheduleRspInt={scheduleRspInterrupt} dpPending={dpInterruptPending} " +
                    $"rspIntDelay={_rspInterruptDelayRemaining} dpDelay={_dpInterruptDelayRemaining}");
            }
        }

        private void FinalizeRspInterrupt()
        {
            uint status = ReadBigEndianWord(SP_STATUS_REG_R);
            if (!_rspTaskLocked)
                status |= SpStatusTaskDone | SpStatusBroke | SpStatusHalt;
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
            if (!_dpCompletionPending)
                return;

            _dpCompletionPending = false;
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] FinalizeDpInterrupt preRaise type={_activeRspTask.Type} pc=0x{Registers.R4300.PC:x8} " +
                    $"miIntr=0x{ReadBigEndianWord(MI_INTR_REG_R):x8} miMask=0x{ReadBigEndianWord(MI_INTR_MASK_REG_R):x8} " +
                    $"dpcStatus=0x{ReadBigEndianWord(DPC_STATUS_REG_R):x8}");
            }
            if (!SuppressDpInterrupt)
                SetMiDpInterrupt(immediate: true);
        }

        private void FinalizeGraphicsTask()
        {
            uint start = ReadBigEndianWord(DPC_START_REG_RW);
            uint end = ReadBigEndianWord(DPC_END_REG_RW);
            uint current = ReadBigEndianWord(DPC_CURRENT_REG_RW);
            if (current == 0)
                current = start;
            TrackFramebufferInfosFromDpcBuffer(current, end);
            uint consumed = ExecuteRdpDisplayList(current, end);
            WriteBigEndianWord(DPC_CURRENT_REG_RW, consumed);

            uint status = ReadBigEndianWord(DPC_STATUS_REG_R);
            if (consumed >= (end & 0x00FFFFF8u))
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
                    $"[N64IO] Graphics task finalized dpcStart=0x{start:x8} dpcCurrent=0x{current:x8} dpcEnd=0x{end:x8} " +
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
            // Mupen's cart ROM DMA write path schedules roughly length/8 cycles.
            // Do not apply the 0x1000 cart-I/O minimum here; OoT streams many small ROM chunks.
            ArmPiDmaCompletion(normalizedLength, minimumDelayCycles: 1u);
        }

        public void PI_RD_LEN_WRITE_EVENT()
        {
            // PI_RD_LEN is the RDRAM -> cart/peripheral direction.
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
            // Cart/peripheral writeback is mostly unimplemented here and Mupen's cart ROM
            // read handler uses a fixed 0x1000-cycle completion.
            ArmPiDmaCompletion(normalizedLength, minimumDelayCycles: PiDmaCyclesMinimum);
        }

        public void PI_STATUS_READ_EVENT()
        {
            if (FastPiStatusPoll && _piInterruptDelayArmed)
                FinalizePiDmaCompletion();

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

            // Bit1 clears the PI interrupt latch, bit0 resets PI status.
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

            ProcessPifJoybusCommands();
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
            // Direct CPU writes to PIF RAM go through the SI completion path.
            // The write is visible immediately; processing and interrupt happen on SI_INT.
            // Ignore emulator-owned boot/reset seeding before the CPU thread is live.
            if (!R4300.R4300_ON)
                return;

            if (_siDmaActive || _siDirectPifWriteActive || _siInterruptDelayArmed)
                return;

            ArmDirectPifWriteCompletion();

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
            bool traceLateSpRegs = IsTraceN64SpDmaEnabled() && IsLateRspKickCpuPc();
            if (TraceN64Io || traceLateSpRegs)
            {
                uint len = ReadBigEndianWord(SP_RD_LEN_REG_RW);
                uint mem = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
                uint dram = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
                Common.Logger.PrintWarningLine(
                    $"[N64SPREG] SP_RD_LEN write len=0x{len:x8} spMem=0x{mem:x8} dram=0x{dram:x8} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
            }
            ExecuteSpDma(isReadFromDram: true);
        }

        public void SP_MEM_ADDR_WRITE_EVENT()
        {
            uint value = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
            bool traceLateSpRegs =
                IsTraceN64SpDmaEnabled() &&
                (IsLateRspKickCpuPc() || (Registers.R4300.PC >= 0x800A0000u && Registers.R4300.PC <= 0x800A2000u));

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
                (suspiciousLowDram || IsLateRspKickCpuPc() || (Registers.R4300.PC >= 0x800A0000u && Registers.R4300.PC <= 0x800A2000u));

            // Always log SP_DRAM_ADDR writes when debugging Mega Man 64
            bool forceTraceSpDram = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SP_DRAM"), "1", StringComparison.Ordinal);
            
            if (!TraceN64Io && !traceLateSpRegs && !forceTraceSpDram)
                return;

            Common.Logger.PrintWarningLine(
                $"[N64SPREG] SP_DRAM_ADDR write value=0x{value:x8} suspiciousLow={suspiciousLowDram} pc=0x{Registers.R4300.PC:x8} {BuildStoreContext()}");
        }

        public void SP_SEMAPHORE_READ_EVENT()
        {
            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP_SEMAPHORE read old=0x{ReadBigEndianWord(SP_SEMAPHORE_REG_R):x8} pc=0x{Registers.R4300.PC:x8}");
            }
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
                (suspiciousLowDram || IsLateRspKickCpuPc() || (Registers.R4300.PC >= 0x800A0000u && Registers.R4300.PC <= 0x800A2000u));

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

            int transferLength = (int)(((lenReg & 0xFFFu) | 7u) + 1u);
            int count = (int)(((lenReg >> 12) & 0xFFu) + 1u);
            int skip = (int)((lenReg >> 20) & 0xFFFu);

            if (transferLength <= 0 || count <= 0)
                return;

            bool traceSpDma = IsTraceN64SpDmaEnabled();
            bool traceDetailedSpWriteDma = traceSpDma && !isReadFromDram;
            bool traceDescriptorOverlap = isReadFromDram
                && IsRspDescriptorDmemAddress(startMemAddr, (uint)Math.Max(1, transferLength));

            if (traceSpDma)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPDMA] start pc=0x{Registers.R4300.PC:x8} " +
                    $"read={isReadFromDram} " +
                    $"spMem=0x{request.MemAddr:x8}->0x{memAddr:x8} dram=0x{request.DramAddr:x8}->0x{dramAddr:x8} " +
                    $"lenReg=0x{lenReg:x8} transferLength=0x{transferLength:x} count=0x{count:x} skip=0x{skip:x}");
            }

            if (traceDescriptorOverlap)
                TraceRspDescriptorDmemSnapshot("pre-dma");

            _spDmaBusy = true;
            WriteBigEndianWord(SP_DMA_BUSY_REG_R, 1);
            uint status = ReadBigEndianWord(SP_STATUS_REG_R) | SpStatusDmaBusy;
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            for (int block = 0; block < count; block++)
            {
                for (int i = 0; i < transferLength; i++)
                {
                    uint spAddress = memBank | ((memAddr + (uint)i) & 0x0FFFu);
                    uint rawRdAddress = dramAddr + (uint)i;
                    uint rdAddress = rawRdAddress & 0x007FFFFFu;

                    if (isReadFromDram)
                    {
                        byte value = ReadUInt8(PhysicalToKseg1(rdAddress));
                        WriteSpMemoryByte(spAddress, value);
                    }
                    else
                    {
                        byte value = ReadSpMemoryByte(spAddress);
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

                if (traceDescriptorOverlap && IsRspDescriptorDmemAddress(memBank | memAddr, (uint)Math.Max(1, transferLength)))
                    TraceRspDescriptorDmemSnapshot($"post-block{block}");

                memAddr = (memAddr + (uint)transferLength) & 0x0FFFu;
                dramAddr = (dramAddr + (uint)(transferLength + skip)) & 0x00FFFFFFu;
            }

            if (traceSpDma)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64SPDMA] end pc=0x{Registers.R4300.PC:x8} " +
                    $"read={isReadFromDram} " +
                    $"startMem=0x{startMemAddr:x4} endMem=0x{memAddr:x4} startDram=0x{startDramAddr:x8} endDram=0x{dramAddr:x8}");
            }

            WriteBigEndianWord(SP_MEM_ADDR_REG_RW, memAddr & 0x0FFFu);
            WriteBigEndianWord(SP_DRAM_ADDR_REG_RW, dramAddr & 0x00FFFFFFu);

            if (isReadFromDram && (TraceN64Io || TraceRspTaskDmem))
            {
                TraceRspDmemWindowAfterReadDma(startMemAddr, request.DramAddr & 0x00FFFFF8u, transferLength, count, skip);
                TraceRspImemWindowAfterDma(startMemAddr, request.DramAddr & 0x00FFFFF8u, transferLength, count, skip);
            }

            // Mupen resets SP_RD_LEN_REG after either DMA direction completes.
            // Some microcode polls mfc0 $2 regardless of which DMA path was used.
            WriteBigEndianWord(SP_RD_LEN_REG_RW, 0x00000FF8u);

            _spDmaDelayArmed = true;
            _spDmaDelayRemaining = Math.Max(1u, (uint)((count * transferLength) / 8));
        }

        private byte ReadSpMemoryByte(uint spAddress)
        {
            return SP_MEM_RW[spAddress & 0x1FFFu];
        }

        private void WriteSpMemoryByte(uint spAddress, byte value)
        {
            uint masked = spAddress & 0x1FFFu;
            byte oldValue = SP_MEM_RW[masked];
            SP_MEM_RW[masked] = value;
            if (IsRspDescriptorDmemAddress(spAddress))
                TraceRspDescriptorDmemWrite(spAddress & 0x0FFFu, oldValue, value, "direct");
        }

        public void SP_STATUS_WRITE_EVENT()
        {
            uint writeValue = ReadBigEndianWord(SP_STATUS_REG_W);
            uint status = ReadBigEndianWord(SP_STATUS_REG_R);
            bool rspEventPending = _rspInterruptDelayArmed;
            bool traceLateSpStatus = IsTraceN64SpDmaEnabled() && IsLateRspKickCpuPc();
            if (TraceN64Io || traceLateSpStatus)
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
            if ((writeValue & 0x00000018u) == 0x00000008u) ClearMiSpInterrupt(); // CLR_INTR
            if ((writeValue & 0x00000018u) == 0x00000010u) SetMiSpInterrupt();   // SET_INTR
            ApplySpStatusPair(ref status, writeValue, 0x00000060u, 0x00000020u, 0x00000040u, 0x00000020u); // SSTEP
            ApplySpStatusPair(ref status, writeValue, 0x00000180u, 0x00000080u, 0x00000100u, SpStatusIntrBreak);
            ApplySpStatusPair(ref status, writeValue, 0x00000600u, 0x00000200u, 0x00000400u, 0x00000080u); // SIG0
            ApplySpStatusPair(ref status, writeValue, 0x00001800u, 0x00000800u, 0x00001000u, 0x00000100u); // SIG1
            ApplySpStatusPair(ref status, writeValue, 0x00006000u, 0x00002000u, 0x00004000u, 0x00000200u); // SIG2
            ApplySpStatusPair(ref status, writeValue, 0x00018000u, 0x00008000u, 0x00010000u, 0x00000400u); // SIG3
            ApplySpStatusPair(ref status, writeValue, 0x00060000u, 0x00020000u, 0x00040000u, 0x00000800u); // SIG4
            ApplySpStatusPair(ref status, writeValue, 0x00180000u, 0x00080000u, 0x00100000u, 0x00001000u); // SIG5
            ApplySpStatusPair(ref status, writeValue, 0x00600000u, 0x00200000u, 0x00400000u, 0x00002000u); // SIG6
            ApplySpStatusPair(ref status, writeValue, 0x01800000u, 0x00800000u, 0x01000000u, 0x00004000u); // SIG7

            // Mirror the control-bit effects into the visible status register before dispatching RSP work.
            // The interpreter polls SP_STATUS via mfc0 c4 while a task is live; if we leave the old HALT/BROKE
            // bits latched until after dispatch returns, the microcode sees a stale status image and can loop
            // on conditions which Mupen clears immediately.
            WriteBigEndianWord(SP_STATUS_REG_R, status);

            if (_rspTaskLocked && rspEventPending)
            {
                if (TraceN64Io || traceLateSpStatus)
                    Common.Logger.PrintWarningLine($"[N64IO] SP_STATUS new=0x{status:x8} (task locked)");
                return;
            }

            bool rspShouldStart = !_rspTaskActive
                && !_rspTaskDispatching
                && (_rspTaskLocked || clearHalt || clearBroke)
                && (status & SpStatusHalt) == 0;

            if ((TraceN64Io || traceLateSpStatus) && (clearHalt || clearBroke || (writeValue & 0x00000118u) != 0))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] SP_STATUS gate clearHalt={clearHalt} setHalt={setHalt} clearBroke={clearBroke} " +
                    $"taskActive={_rspTaskActive} taskLocked={_rspTaskLocked} dispatching={_rspTaskDispatching} " +
                    $"rspEventPending={rspEventPending} shouldStart={rspShouldStart} " +
                    $"statusAfterCtrl=0x{status:x8} pc=0x{Registers.R4300.PC:x8}");
            }

            if (rspShouldStart && (TraceN64Io || TraceRspTaskDmem || traceLateSpStatus))
                TraceRspTaskHeaderWords(0x0FC0u, "sp-kick");
            else if ((TraceN64Io || traceLateSpStatus) && (clearHalt || clearBroke))
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
            uint status = ReadBigEndianWord(DPC_STATUS_REG_R);
            if (!MupenStyleDpcStatus)
                status |= DpcStatusStartValid | DpcStatusStartGclk | DpcStatusCbufReady;
            WriteBigEndianWord(DPC_STATUS_REG_R, status);

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
            uint start = ReadBigEndianWord(DPC_START_REG_RW);
            uint current = ReadBigEndianWord(DPC_CURRENT_REG_RW);
            if (current == 0)
                current = start;
            TrackFramebufferInfosFromDpcBuffer(current, value);
            uint consumed = ExecuteRdpDisplayList(current, value);
            WriteBigEndianWord(DPC_CURRENT_REG_RW, consumed);
            uint status = ReadBigEndianWord(DPC_STATUS_REG_R);
            status |= DpcStatusStartGclk;
            if (consumed >= (value & 0x00FFFFF8u))
                status &= ~(DpcStatusStartValid | DpcStatusEndValid | DpcStatusCbufReady);
            WriteBigEndianWord(DPC_STATUS_REG_R, status);
            uint span = consumed > current ? consumed - current : 0u;
            if (span != 0)
            {
                if (MupenStyleDpcStatus && !SuppressDpInterrupt)
                {
                    SetMiDpInterrupt();
                }
                else
                {
                    _dpCompletionPending = true;
                    _dpInterruptDelayArmed = true;
                    _dpInterruptDelayRemaining = Math.Max(1u, span / 8u);
                }
            }

            if (TraceN64Io)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] DPC_END queued start=0x{ReadBigEndianWord(DPC_START_REG_RW):x8} current=0x{current:x8} end=0x{value:x8} " +
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
            NoteRspTaskDispatch(task.Type);

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
            _rspTaskLocked = false;
            _rspInterruptDelayArmed = false;

            if (task.Type == 1 && !SuppressDpInterrupt)
                SetMiDpInterrupt();

            status |= SpStatusHalt | SpStatusBroke;
            WriteBigEndianWord(SP_STATUS_REG_R, status);
            ArmSynchronousRspCompletion(ref status);
        }

        private void TryDispatchRspTaskInterpreter(ref uint status)
        {
            bool hasTask = TryReadRspTaskFromDmem(out RspTask task);

            if (!hasTask && IsIplRawRspKick())
            {
                // The IPL clears SP halt before any OS task exists. Running the raw RSP
                // interpreter synchronously here can trap the CPU thread in boot bring-up,
                // so acknowledge the kick as an already-completed boot helper.
                status |= SpStatusHalt | SpStatusBroke;
                WriteBigEndianWord(SP_STATUS_REG_R, status);
                return;
            }

            if (hasTask && EnableRspInterpreterGraphicsOnly && task.Type != 1)
            {
                if (EnableRspTaskHleDispatcher)
                    TryDispatchRspTaskHle(ref status);
                return;
            }

            _rspKickCount++;
            if (hasTask)
                NoteRspTaskDispatch(task.Type);
            _activeRspTask = hasTask ? task : default;

            if (hasTask && (TraceN64Io || TraceRspTaskDmem))
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP interpreter dispatch type={task.Type} flags=0x{task.Flags:x8} " +
                    $"ucode=0x{task.Ucode:x8}/0x{task.UcodeSize:x} " +
                    $"ucodeData=0x{task.UcodeData:x8}/0x{task.UcodeDataSize:x} " +
                    $"data=0x{task.DataPtr:x8}/0x{task.DataSize:x} " +
                    $"yield=0x{task.YieldDataPtr:x8}/0x{task.YieldDataSize:x} " +
                    $"pc=0x{Registers.R4300.PC:x8}");
            }
            else if (TraceN64Io || TraceRspTaskDmem)
            {
                Common.Logger.PrintWarningLine(
                    $"[N64IO] RSP interpreter raw dispatch pc=0x{Registers.R4300.PC:x8} rspPc=0x{ReadRspPc():x3} " +
                    $"spStatus=0x{status:x8} (no valid OSTask at DMEM+0xFC0)");
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
                    $"[N64IO] RSP interpreter task type={(hasTask ? task.Type : 0u)} validTask={hasTask} " +
                    $"executed={executedInstructions} completed={completed} stop='{stopReason}' pc=0x{Registers.R4300.PC:x8}");
            }

            if (!completed)
            {
                bool allowHleFallback = hasTask && EnableRspTaskHleDispatcher && (task.Type != 1 || AllowGraphicsRspHleFallback);
                if (allowHleFallback)
                {
                    if (!_warnedRspInterpreterFallback)
                    {
                        _warnedRspInterpreterFallback = true;
                        Common.Logger.PrintWarningLine(
                            $"[N64] RSP interpreter hit an unimplemented path ({stopReason}); falling back to task HLE.");
                    }

                    TryDispatchRspTaskHle(ref status);
                    return;
                }

                if (hasTask && task.Type == 1)
                {
                    if (!_warnedRspGraphicsFailLoud)
                    {
                        _warnedRspGraphicsFailLoud = true;
                        Common.Logger.PrintWarningLine(
                            $"[N64] Graphics RSP task failed in interpreter ({stopReason}); not falling back to fake HLE. " +
                            "A real producer-side fix is required before framebuffer selection can be correct.");
                    }

                    status |= SpStatusHalt | SpStatusBroke;
                    WriteBigEndianWord(SP_STATUS_REG_R, status);
                    _rspTaskActive = false;
                    _rspTaskLocked = false;
                    _rspInterruptDelayArmed = false;
                    _dpInterruptDelayArmed = false;
                    SetMiSpInterrupt(immediate: true);
                }

                return;
            }

            if (string.Equals(stopReason, "break", StringComparison.Ordinal))
            {
                status = ReadBigEndianWord(SP_STATUS_REG_R) | SpStatusHalt | SpStatusBroke;
                WriteBigEndianWord(SP_STATUS_REG_R, status);
            }

            _rspTaskLocked = false;
            _rspInterruptDelayArmed = false;

            ArmSynchronousRspCompletion(ref status);
        }

        private static bool IsIplRawRspKick()
        {
            uint pc = Registers.R4300.PC;
            return pc >= 0xA4000000u && pc <= 0xA4001FFFu;
        }

        private void NoteRspTaskDispatch(uint taskType)
        {
            switch (taskType)
            {
                case 1u:
                    Interlocked.Increment(ref _rspGraphicsTaskCount);
                    break;
                case 2u:
                    Interlocked.Increment(ref _rspAudioTaskCount);
                    break;
                default:
                    Interlocked.Increment(ref _rspOtherTaskCount);
                    break;
            }
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
            return ((uint)SP_MEM_RW[index] << 24)
                 | ((uint)SP_MEM_RW[(index + 1) & 0x0FFFu] << 16)
                 | ((uint)SP_MEM_RW[(index + 2) & 0x0FFFu] << 8)
                 | SP_MEM_RW[(index + 3) & 0x0FFFu];
        }

        internal uint ReadSpImemWord(uint imemOffset)
        {
            uint index = 0x1000u | (imemOffset & 0x0FFFu);
            return ((uint)SP_MEM_RW[index] << 24)
                 | ((uint)SP_MEM_RW[(index + 1) & 0x1FFFu] << 16)
                 | ((uint)SP_MEM_RW[(index + 2) & 0x1FFFu] << 8)
                 | SP_MEM_RW[(index + 3) & 0x1FFFu];
        }

        internal uint ReadSpMemoryWord(uint spAddress)
        {
            uint offset = spAddress & 0x0FFFu;
            return (spAddress & 0x1000u) != 0
                ? ReadSpImemWord(offset)
                : ReadSpDmemWord(offset);
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

            const uint stackWindowStart = 0x02B0u;
            const uint stackWindowEndInclusive = 0x02D8u;
            const uint descriptorWindowStart = 0x0410u;
            const uint descriptorWindowEndInclusive = 0x0428u;
            bool touchedStackWindow = false;
            bool touchedDescriptorWindow = false;

            for (int block = 0; block < count && !(touchedStackWindow && touchedDescriptorWindow); block++)
            {
                uint blockStart = (startMemAddr + (uint)(block * transferLength)) & 0x1FFFu;
                uint blockEnd = blockStart + (uint)Math.Max(transferLength - 1, 0);
                if (blockStart <= stackWindowEndInclusive && blockEnd >= stackWindowStart)
                    touchedStackWindow = true;
                if (blockStart <= descriptorWindowEndInclusive && blockEnd >= descriptorWindowStart)
                    touchedDescriptorWindow = true;
            }

            if (!touchedStackWindow && !touchedDescriptorWindow)
                return;

            _traceRspDmaWindowLogCount++;
            uint src = dramAddr & 0x00FFFFF8u;
            string descriptorSources = touchedDescriptorWindow
                ? BuildRspDmaSourceMappingSummary(startMemAddr, src, transferLength, count, skip,
                    0x0410u, 0x0414u, 0x0418u, 0x041Cu, 0x0420u, 0x0424u, 0x0428u)
                : string.Empty;
            Common.Logger.PrintWarningLine(
                $"[N64RSPDMEMDMA] startMem=0x{startMemAddr:x4} dram=0x{src:x8} len=0x{transferLength:x} count=0x{count:x} skip=0x{skip:x} " +
                $"srcBytes={FormatPhysicalByteSpan(src, 16)} " +
                $"srcW0=0x{ReadUInt32Physical(src + 0x00):x8} srcW4=0x{ReadUInt32Physical(src + 0x04):x8} srcW8=0x{ReadUInt32Physical(src + 0x08):x8} srcWc=0x{ReadUInt32Physical(src + 0x0c):x8} " +
                descriptorSources +
                $"dmem2b0=0x{ReadSpDmemWord(0x02B0):x8} dmem2b4=0x{ReadSpDmemWord(0x02B4):x8} dmem2b8=0x{ReadSpDmemWord(0x02B8):x8} dmem2bc=0x{ReadSpDmemWord(0x02BC):x8} " +
                $"dmem410=0x{ReadSpDmemWord(0x0410):x8} dmem414=0x{ReadSpDmemWord(0x0414):x8} dmem418=0x{ReadSpDmemWord(0x0418):x8} " +
                $"dmem41c=0x{ReadSpDmemWord(0x041C):x8} dmem420=0x{ReadSpDmemWord(0x0420):x8} dmem424=0x{ReadSpDmemWord(0x0424):x8} dmem428=0x{ReadSpDmemWord(0x0428):x8}");
        }

        private string BuildRspDmaSourceMappingSummary(uint startMemAddr, uint startDramAddr, int transferLength, int count, int skip, params uint[] dmemWords)
        {
            if (dmemWords == null || dmemWords.Length == 0)
                return string.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (uint dmemWord in dmemWords)
            {
                if (!TryMapRspDmaSourceForDmemAddress(startMemAddr, startDramAddr, transferLength, count, skip, dmemWord, out uint sourcePhysical))
                    continue;

                sb.Append($"src{dmemWord:x3}=0x{sourcePhysical:x8} ");
                sb.Append($"src{dmemWord:x3}Bytes={FormatPhysicalByteSpan(sourcePhysical, 4)} ");
                sb.Append($"src{dmemWord:x3}W=0x{ReadUInt32Physical(sourcePhysical):x8} ");
            }

            return sb.ToString();
        }

        private static bool TryMapRspDmaSourceForDmemAddress(uint startMemAddr, uint startDramAddr, int transferLength, int count, int skip, uint targetDmemAddress, out uint sourcePhysical)
        {
            sourcePhysical = 0;

            uint target = targetDmemAddress & 0x0FFFu;
            uint memBase = startMemAddr & 0x0FFFu;
            uint dramBase = startDramAddr & 0x00FFFFF8u;
            if (transferLength <= 0 || count <= 0)
                return false;

            for (int block = 0; block < count; block++)
            {
                uint blockMem = memBase;
                uint blockDram = dramBase;
                for (int i = 0; i < transferLength; i++)
                {
                    uint spAddress = (blockMem + (uint)i) & 0x0FFFu;
                    if (spAddress == target)
                    {
                        sourcePhysical = (blockDram + (uint)i) & 0x00FFFFFFu;
                        return true;
                    }
                }

                memBase = (memBase + (uint)transferLength) & 0x0FFFu;
                dramBase = (dramBase + (uint)(transferLength + skip)) & 0x00FFFFFFu;
            }

            return false;
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

        internal bool IsRspDmaDelayArmed()
        {
            return _spDmaDelayArmed;
        }

        internal uint GetRspDmaDelayRemaining()
        {
            return _spDmaDelayRemaining;
        }

        internal bool HasQueuedRspDma()
        {
            return _spQueuedDmaValid;
        }

        internal ulong GetRspProgressSignature()
        {
            ulong signature = ReadBigEndianWord(SP_MEM_ADDR_REG_RW);
            signature = (signature * 1099511628211ul) ^ ReadBigEndianWord(SP_DRAM_ADDR_REG_RW);
            signature = (signature * 1099511628211ul) ^ ReadBigEndianWord(SP_RD_LEN_REG_RW);
            signature = (signature * 1099511628211ul) ^ ReadBigEndianWord(SP_WR_LEN_REG_RW);
            signature = (signature * 1099511628211ul) ^ ReadBigEndianWord(SP_STATUS_REG_R);
            signature = (signature * 1099511628211ul) ^ ReadBigEndianWord(SP_DMA_FULL_REG_R);
            signature = (signature * 1099511628211ul) ^ ReadBigEndianWord(SP_DMA_BUSY_REG_R);
            signature = (signature * 1099511628211ul) ^ (_spDmaDelayArmed ? 1ul : 0ul);
            signature = (signature * 1099511628211ul) ^ _spDmaDelayRemaining;
            signature = (signature * 1099511628211ul) ^ (_spQueuedDmaValid ? 1ul : 0ul);
            return signature;
        }

        internal bool HasActiveRspTask()
        {
            return _activeRspTask.Type >= 1 && _activeRspTask.Type <= 4;
        }

        internal uint GetActiveRspTaskType()
        {
            return _activeRspTask.Type;
        }

        internal void WriteSpDmemWord(uint dmemOffset, uint value)
        {
            uint index = dmemOffset & 0x0FFFu;
            byte b0 = (byte)(value >> 24);
            byte b1 = (byte)(value >> 16);
            byte b2 = (byte)(value >> 8);
            byte b3 = (byte)value;

            byte old0 = SP_MEM_RW[index];
            byte old1 = SP_MEM_RW[(index + 1) & 0x0FFFu];
            byte old2 = SP_MEM_RW[(index + 2) & 0x0FFFu];
            byte old3 = SP_MEM_RW[(index + 3) & 0x0FFFu];

            SP_MEM_RW[index] = b0;
            SP_MEM_RW[(index + 1) & 0x0FFFu] = b1;
            SP_MEM_RW[(index + 2) & 0x0FFFu] = b2;
            SP_MEM_RW[(index + 3) & 0x0FFFu] = b3;

            if (IsRspDescriptorDmemAddress(index, 4))
            {
                TraceRspDescriptorDmemWrite(index, old0, b0, "word");
                TraceRspDescriptorDmemWrite((index + 1) & 0x0FFFu, old1, b1, "word");
                TraceRspDescriptorDmemWrite((index + 2) & 0x0FFFu, old2, b2, "word");
                TraceRspDescriptorDmemWrite((index + 3) & 0x0FFFu, old3, b3, "word");
                TraceRspDescriptorDmemSnapshot("post-word");
            }
        }

        internal void WriteSpImemWord(uint imemOffset, uint value)
        {
            uint index = 0x1000u | (imemOffset & 0x0FFFu);
            SP_MEM_RW[index] = (byte)(value >> 24);
            SP_MEM_RW[(index + 1) & 0x1FFFu] = (byte)(value >> 16);
            SP_MEM_RW[(index + 2) & 0x1FFFu] = (byte)(value >> 8);
            SP_MEM_RW[(index + 3) & 0x1FFFu] = (byte)value;
        }

        internal void WriteSpMemoryWord(uint spAddress, uint value)
        {
            uint offset = spAddress & 0x0FFFu;
            if ((spAddress & 0x1000u) != 0)
                WriteSpImemWord(offset, value);
            else
                WriteSpDmemWord(offset, value);
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
            uint value;
            switch (reg & 0x1F)
            {
                case 0: value = ReadBigEndianWord(SP_MEM_ADDR_REG_RW); break;
                case 1: value = ReadBigEndianWord(SP_DRAM_ADDR_REG_RW); break;
                case 2: value = ReadBigEndianWord(SP_RD_LEN_REG_RW); break;
                case 3: value = ReadBigEndianWord(SP_WR_LEN_REG_RW); break;
                case 4: value = ReadBigEndianWord(SP_STATUS_REG_R); break;
                case 5: value = ReadBigEndianWord(SP_DMA_FULL_REG_R); break;
                case 6: value = ReadBigEndianWord(SP_DMA_BUSY_REG_R); break;
                case 7:
                    value = ReadBigEndianWord(SP_SEMAPHORE_REG_R);
                    break;
                case 8: value = ReadBigEndianWord(DPC_START_REG_RW); break;
                case 9: value = ReadBigEndianWord(DPC_END_REG_RW); break;
                case 10: value = ReadBigEndianWord(DPC_CURRENT_REG_RW); break;
                case 11: value = ReadBigEndianWord(DPC_STATUS_REG_R); break;
                case 12: value = ReadBigEndianWord(DPC_CLOCK_REG_RW); break;
                case 13: value = ReadBigEndianWord(DPC_BUFBUSY_REG_RW); break;
                case 14: value = ReadBigEndianWord(DPC_PIPEBUSY_REG_RW); break;
                case 15: value = ReadBigEndianWord(DPC_TMEM_REG_RW); break;
                default: value = 0; break;
            }

            if (TraceN64Io || IsTraceN64SpDmaEnabled())
            {
                Common.Logger.PrintWarningLine(
                    $"[N64RSPCP0R] reg={(reg & 0x1F)} value=0x{value:x8} rspPc=0x{ReadRspPc():x3} cpuPc=0x{Registers.R4300.PC:x8} " +
                    $"spMem=0x{ReadBigEndianWord(SP_MEM_ADDR_REG_RW):x8} spDram=0x{ReadBigEndianWord(SP_DRAM_ADDR_REG_RW):x8} " +
                    $"rdLen=0x{ReadBigEndianWord(SP_RD_LEN_REG_RW):x8} wrLen=0x{ReadBigEndianWord(SP_WR_LEN_REG_RW):x8} " +
                    $"dmaFull=0x{ReadBigEndianWord(SP_DMA_FULL_REG_R):x8} dmaBusy=0x{ReadBigEndianWord(SP_DMA_BUSY_REG_R):x8} " +
                    $"dpcCur=0x{ReadBigEndianWord(DPC_CURRENT_REG_RW):x8} dpcEnd=0x{ReadBigEndianWord(DPC_END_REG_RW):x8}");
            }

            return value;
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

        private static void ApplySpStatusPair(ref uint status, uint value, uint pairMask, uint clearValue, uint setValue, uint statusBit)
        {
            uint pair = value & pairMask;
            if (pair == clearValue)
                status &= ~statusBit;
            else if (pair == setValue)
                status |= statusBit;
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
                RegisterFramebufferInfoFromViRegisters(value);
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
                            PIFRAM[rxIndex + 0] = (byte)(N64ControllerButtons >> 8);
                            PIFRAM[rxIndex + 1] = (byte)N64ControllerButtons;
                            PIFRAM[rxIndex + 2] = unchecked((byte)N64ControllerAnalogX);
                            PIFRAM[rxIndex + 3] = unchecked((byte)N64ControllerAnalogY);
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
            public int ReadBaseOffset;
            public int WriteBaseOffset;

            public MemoryEvent ReadEvent;
            public MemoryEvent WriteEvent;

            public MemEntry(uint StartAddress, uint EndAddress, byte[] ReadArray, byte[] WriteArray, string Name, MemoryEvent ReadEvent = null, MemoryEvent WriteEvent = null, int ReadBaseOffset = 0, int WriteBaseOffset = 0)
            {
                this.StartAddress = StartAddress;
                this.EndAddress   = EndAddress;
                this.ReadArray    = ReadArray;
                this.WriteArray   = WriteArray;
                this.Name         = Name;
                this.ReadBaseOffset = ReadBaseOffset;
                this.WriteBaseOffset = WriteBaseOffset;
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
                    SP_MEM_RW,
                    SP_MEM_RW,
                    "SP_DMEM_MIRROR",
                    null,
                    null,
                    0x0000,
                    0x0000);
                return true;
            }

            entry = new MemEntry(
                mirrorBase,
                mirrorBase + 0x0FFFu,
                SP_MEM_RW,
                SP_MEM_RW,
                "SP_IMEM_MIRROR",
                null,
                null,
                0x1000,
                0x1000);
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

                uint regOffset = nonCachedIndex - Entry.StartAddress;
                int offset = ResolveArrayOffset(Entry.ReadArray, regOffset, Entry.ReadBaseOffset);
                byte value = Entry.ReadArray[offset];
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

                uint regOffset = nonCachedIndex - Entry.StartAddress;
                int offset = ResolveArrayOffset(Entry.WriteArray, regOffset, Entry.WriteBaseOffset);
                byte oldValue = Entry.WriteArray[offset];
                Entry.WriteArray[offset] = value;

                if (ReferenceEquals(Entry.WriteArray, SP_MEM_RW)
                    && IsRspDescriptorDmemAddress((uint)Entry.WriteBaseOffset | (regOffset & 0x0FFFu)))
                {
                    TraceRspDescriptorDmemWrite((uint)Entry.WriteBaseOffset | (regOffset & 0x0FFFu), oldValue, value, "direct");
                }

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
            try
            {
                uint physical = ToPhysicalAddress(index, isWrite: false);
                if (IsCartridgeBusPhysicalAddress(physical))
                    return ReadCartridgeBusByte(physical);
            }
            catch
            {
                // Preserve existing exception behavior below.
            }

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
                if (IsCartridgeBusPhysicalAddress(physical))
                {
                    HandleCartridgeBusWrite(physical, value, 1);
                    return;
                }

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
            try
            {
                uint physical = ToPhysicalAddress(index, isWrite: false);
                if (IsCartridgeBusPhysicalAddress(physical))
                    return ReadCartridgeBusUInt16(physical);
                if (physical + 1u < RDRAM.Length)
                {
                    return (ushort)((RDRAM[physical] << 8) | RDRAM[physical + 1u]);
                }
            }
            catch
            {
                // Preserve existing exception behavior below.
            }

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
                if (IsCartridgeBusPhysicalAddress(physical))
                {
                    HandleCartridgeBusWrite(physical, value, 2);
                    return;
                }

                if ((TraceExceptionVectorWrites && IsExceptionVectorPhysicalAddress(physical, 2))
                    || (TraceLowRamMutationWrites && IsLowRamDiagnosticPhysicalAddress(physical, 2)))
                    oldValue = ReadUInt16(index);

                if (physical + 1u < RDRAM.Length
                    && !TraceWatchAddress.HasValue
                    && !ShouldTraceWatchRange(index, physical, 2)
                    && !ShouldTraceSpRegisterStore(physical)
                    && !(physical <= 0x00000300u
                        && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal)))
                {
                    RDRAM[physical] = (byte)(value >> 8);
                    RDRAM[physical + 1u] = (byte)value;
                    NoteRdramWriteRange(physical, 2);
                    TraceLowRamMutationWrite("write16", index, physical, oldValue, value, 2);
                    TraceExceptionVectorWrite("write16", index, physical, oldValue, value, 2);
                    return;
                }
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
                if (IsCartridgeBusPhysicalAddress(physical))
                    return ReadCartridgeBusUInt32(physical);
                if (physical + 3u < RDRAM.Length)
                {
                    uint value = ((uint)RDRAM[physical] << 24)
                        | ((uint)RDRAM[physical + 1u] << 16)
                        | ((uint)RDRAM[physical + 2u] << 8)
                        | RDRAM[physical + 3u];
                    if (ShouldTraceWatchRange(index, physical))
                        TraceWatchRangeAccess("read32", index, physical, value);
                    return value;
                }
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

        internal uint ReadUInt32PhysicalFast(uint physical)
        {
            physical &= 0x1FFFFFFFu;
            if (physical + 3u < RDRAM.Length)
            {
                return ((uint)RDRAM[physical] << 24)
                    | ((uint)RDRAM[physical + 1u] << 16)
                    | ((uint)RDRAM[physical + 2u] << 8)
                    | RDRAM[physical + 3u];
            }

            return ReadUInt32(0xA0000000u | physical);
        }

        internal bool TryReadRdramUInt32PhysicalFast(uint physical, out uint value)
        {
            physical &= 0x1FFFFFFFu;
            if (physical + 3u >= RDRAM.Length)
            {
                value = 0;
                return false;
            }

            value = ((uint)RDRAM[physical] << 24)
                | ((uint)RDRAM[physical + 1u] << 16)
                | ((uint)RDRAM[physical + 2u] << 8)
                | RDRAM[physical + 3u];
            return true;
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
                if (IsCartridgeBusPhysicalAddress(physical))
                {
                    HandleCartridgeBusWrite(physical, value, 4);
                    return;
                }

                if ((TraceExceptionVectorWrites && IsExceptionVectorPhysicalAddress(physical, 4))
                    || (TraceLowRamMutationWrites && IsLowRamDiagnosticPhysicalAddress(physical, 4)))
                    oldValue = ReadUInt32(index);

                if (physical + 3u < RDRAM.Length
                    && !TraceWatchAddress.HasValue
                    && !TraceMegaCallbackBlock
                    && !TraceMegaFatalBlock
                    && !TraceMegaStatusBlock
                    && !TraceSm64SlotWrites
                    && !ShouldTraceWatchRange(index, physical)
                    && !ShouldTraceSpRegisterStore(physical)
                    && !(physical <= 0x00000300u
                        && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal)))
                {
                    RDRAM[physical] = (byte)(value >> 24);
                    RDRAM[physical + 1u] = (byte)(value >> 16);
                    RDRAM[physical + 2u] = (byte)(value >> 8);
                    RDRAM[physical + 3u] = (byte)value;
                    NoteRdramWriteRange(physical, 4);
                    TraceLowRamMutationWrite("write32", index, physical, oldValue, value, 4);
                    TraceExceptionVectorWrite("write32", index, physical, oldValue, value, 4);
                    return;
                }
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

        private static int ResolveArrayOffset(byte[] array, uint logicalOffset, int baseOffset = 0)
        {
            if (array.Length == 0)
                throw new IndexOutOfRangeException("Mapped memory region has zero length.");

            uint absoluteOffset = (uint)baseOffset + logicalOffset;
            if (absoluteOffset < (uint)array.Length)
                return (int)absoluteOffset;

            // Cartridge and some register regions are mirrored over larger address windows.
            return (int)(absoluteOffset % (uint)array.Length);
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

                int dstOff = ResolveArrayOffset(dstEntry.WriteArray, dest - dstEntry.StartAddress, dstEntry.WriteBaseOffset);
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
                    srcOff = ResolveArrayOffset(srcEntry.ReadArray, src - srcEntry.StartAddress, srcEntry.ReadBaseOffset);
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

                bool touchesSpDescriptorViaBulkCopy =
                    ReferenceEquals(dstEntry.WriteArray, SP_MEM_RW)
                    && dstEntry.WriteBaseOffset == 0
                    && RangeOverlaps(dest, (uint)Math.Max(1, chunk), 0x04000410u, 0x04000428u);

                if (touchesSpDescriptorViaBulkCopy)
                    TraceRspDescriptorDmemSnapshot("bulk-pre");

                if (copyCount > 0)
                {
                    bool srcIsSpDmem = ReferenceEquals(srcEntry.ReadArray, SP_MEM_RW) && srcEntry.ReadBaseOffset == 0;
                    bool srcIsSpImem = ReferenceEquals(srcEntry.ReadArray, SP_MEM_RW) && srcEntry.ReadBaseOffset == 0x1000;
                    bool dstIsSpDmem = ReferenceEquals(dstEntry.WriteArray, SP_MEM_RW) && dstEntry.WriteBaseOffset == 0;
                    bool dstIsSpImem = ReferenceEquals(dstEntry.WriteArray, SP_MEM_RW) && dstEntry.WriteBaseOffset == 0x1000;

                    // Keep SP memory accesses on the same byte-addressed helper path used by
                    // CPU/RSP MMIO instead of raw BlockCopy. This matches the rest of the SP
                    // memory model and avoids bulk copies bypassing SP-specific semantics.
                    if (srcIsSpDmem || srcIsSpImem || dstIsSpDmem || dstIsSpImem)
                    {
                        uint srcStart = src - srcEntry.StartAddress;
                        uint dstStart = dest - dstEntry.StartAddress;

                        for (int i = 0; i < copyCount; i++)
                        {
                            byte value;
                            if (srcIsSpDmem)
                                value = ReadSpMemoryByte((srcStart + (uint)i) & 0x0FFFu);
                            else if (srcIsSpImem)
                                value = ReadSpMemoryByte(0x1000u | ((srcStart + (uint)i) & 0x0FFFu));
                            else
                                value = srcArray[srcOff + i];

                            if (dstIsSpDmem)
                                WriteSpMemoryByte((dstStart + (uint)i) & 0x0FFFu, value);
                            else if (dstIsSpImem)
                                WriteSpMemoryByte(0x1000u | ((dstStart + (uint)i) & 0x0FFFu), value);
                            else
                                dstEntry.WriteArray[dstOff + i] = value;
                        }
                    }
                    else
                    {
                        Buffer.BlockCopy(srcArray, srcOff, dstEntry.WriteArray, dstOff, copyCount);
                    }
                }

                if (zeroFillFromRom)
                {
                    bool dstIsSpDmem = ReferenceEquals(dstEntry.WriteArray, SP_MEM_RW) && dstEntry.WriteBaseOffset == 0;
                    bool dstIsSpImem = ReferenceEquals(dstEntry.WriteArray, SP_MEM_RW) && dstEntry.WriteBaseOffset == 0x1000;
                    if (dstIsSpDmem || dstIsSpImem)
                    {
                        uint dstStart = dest - dstEntry.StartAddress;
                        for (int i = copyCount; i < chunk; i++)
                        {
                            if (dstIsSpDmem)
                                WriteSpMemoryByte((dstStart + (uint)i) & 0x0FFFu, 0);
                            else
                                WriteSpMemoryByte(0x1000u | ((dstStart + (uint)i) & 0x0FFFu), 0);
                        }
                    }
                    else
                    {
                        Array.Clear(dstEntry.WriteArray, dstOff + copyCount, chunk - copyCount);
                    }

                    if (TracePiInterruptLifecycle
                        || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
                    {
                        Common.Logger.PrintWarningLine(
                            $"[N64PIDMA] rom-zero-fill pc=0x{Registers.R4300.PC:x8} " +
                            $"src=0x{src:x8} dest=0x{dest:x8} chunk=0x{chunk:x} copied=0x{copyCount:x} " +
                            $"cartWindow={srcEntry.Name} romSize=0x{_rom.Length:x}");
                    }
                }

                if (touchesSpDescriptorViaBulkCopy && (TraceN64Io || TraceRspTaskDmem))
                {
                    Common.Logger.PrintWarningLine(
                        $"[N64RSPDMEMBULK] pc=0x{Registers.R4300.PC:x8} dest=0x{dest:x8} src=0x{src:x8} " +
                        $"chunk=0x{chunk:x} copied=0x{copyCount:x} zeroFill={zeroFillFromRom}");
                    TraceRspDescriptorDmemSnapshot("bulk-post");
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
