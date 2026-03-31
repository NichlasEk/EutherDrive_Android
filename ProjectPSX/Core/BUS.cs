using ProjectPSX.Devices;
using ProjectPSX.Devices.Expansion;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using ProjectPSX.IO;

namespace ProjectPSX {

public class BUS {
        public readonly struct PerfSnapshot {
            public readonly int TickCalls;
            public readonly int TickCycles;
            public readonly int ReadOpsFast;
            public readonly int ReadOpsMmio;
            public readonly int WriteOpsFast;
            public readonly int WriteOpsMmio;
            public readonly int Load32Ram;
            public readonly int Load32Ex1;
            public readonly int Load32Scratchpad;
            public readonly int Load32Bios;
            public readonly int Load32Mmio;
            public readonly int Write32Ram;
            public readonly int Write16Ram;
            public readonly int Write8Ram;
            public readonly int Write32Ex1;
            public readonly int Write16Ex1;
            public readonly int Write8Ex1;
            public readonly int Write32Scratchpad;
            public readonly int Write16Scratchpad;
            public readonly int Write8Scratchpad;
            public readonly int Write32Mmio;
            public readonly int Write16Mmio;
            public readonly int Write8Mmio;
            public readonly uint TopMmioReadAddr0;
            public readonly int TopMmioReadCount0;
            public readonly uint TopMmioReadAddr1;
            public readonly int TopMmioReadCount1;
            public readonly uint TopMmioReadAddr2;
            public readonly int TopMmioReadCount2;
            public readonly int RelaxedGpuStatReads;
            public readonly int RelaxedJoyStatusReads;
            public readonly int RelaxedTimer2Reads;
            public readonly int RelaxedInterruptStatusReads;
            public readonly int MmioShadowHits;

            public PerfSnapshot(
                int tickCalls,
                int tickCycles,
                int readOpsFast,
                int readOpsMmio,
                int writeOpsFast,
                int writeOpsMmio,
                int load32Ram,
                int load32Ex1,
                int load32Scratchpad,
                int load32Bios,
                int load32Mmio,
                int write32Ram,
                int write16Ram,
                int write8Ram,
                int write32Ex1,
                int write16Ex1,
                int write8Ex1,
                int write32Scratchpad,
                int write16Scratchpad,
                int write8Scratchpad,
                int write32Mmio,
                int write16Mmio,
                int write8Mmio,
                uint topMmioReadAddr0,
                int topMmioReadCount0,
                uint topMmioReadAddr1,
                int topMmioReadCount1,
                uint topMmioReadAddr2,
                int topMmioReadCount2,
                int relaxedGpuStatReads,
                int relaxedJoyStatusReads,
                int relaxedTimer2Reads,
                int relaxedInterruptStatusReads,
                int mmioShadowHits) {
                TickCalls = tickCalls;
                TickCycles = tickCycles;
                ReadOpsFast = readOpsFast;
                ReadOpsMmio = readOpsMmio;
                WriteOpsFast = writeOpsFast;
                WriteOpsMmio = writeOpsMmio;
                Load32Ram = load32Ram;
                Load32Ex1 = load32Ex1;
                Load32Scratchpad = load32Scratchpad;
                Load32Bios = load32Bios;
                Load32Mmio = load32Mmio;
                Write32Ram = write32Ram;
                Write16Ram = write16Ram;
                Write8Ram = write8Ram;
                Write32Ex1 = write32Ex1;
                Write16Ex1 = write16Ex1;
                Write8Ex1 = write8Ex1;
                Write32Scratchpad = write32Scratchpad;
                Write16Scratchpad = write16Scratchpad;
                Write8Scratchpad = write8Scratchpad;
                Write32Mmio = write32Mmio;
                Write16Mmio = write16Mmio;
                Write8Mmio = write8Mmio;
                TopMmioReadAddr0 = topMmioReadAddr0;
                TopMmioReadCount0 = topMmioReadCount0;
                TopMmioReadAddr1 = topMmioReadAddr1;
                TopMmioReadCount1 = topMmioReadCount1;
                TopMmioReadAddr2 = topMmioReadAddr2;
                TopMmioReadCount2 = topMmioReadCount2;
                RelaxedGpuStatReads = relaxedGpuStatReads;
                RelaxedJoyStatusReads = relaxedJoyStatusReads;
                RelaxedTimer2Reads = relaxedTimer2Reads;
                RelaxedInterruptStatusReads = relaxedInterruptStatusReads;
                MmioShadowHits = mmioShadowHits;
            }
        }

        private const uint Sio1StatusDefault = 0x0000_0805;
        private static readonly bool VerboseBusAccess = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1";
        private static readonly bool RelaxGpuStatusPolling = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_RELAX_GPUSTAT_POLLING") != "0";
        private static readonly bool RelaxJoyStatusPolling = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_RELAX_JOYSTAT_POLLING") != "0";
        private static readonly bool RelaxTimer2Polling = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_RELAX_TIMER2_POLLING") != "0";
        private static readonly uint? TraceRamReadStart = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_READ_START");
        private static readonly uint? TraceRamReadEnd = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_READ_END");
        private static readonly int TraceRamReadLimit = ParseOptionalPositiveInt("EUTHERDRIVE_PSX_TRACE_RAM_READ_LIMIT", 4096);
        private static readonly uint? TraceRamWriteStart = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_START");
        private static readonly uint? TraceRamWriteEnd = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_END");
        private static readonly int TraceRamWriteLimit = ParseOptionalPositiveInt("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_LIMIT", 4096);
        private static readonly bool TraceCdDma = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_TRACE_CD_DMA") == "1";
        private static int s_traceRamReadCount;
        private static int s_traceRamWriteCount;
        private const int SpuTickBatchCycles = 96;
        private const int CpuTightSyncBudgetCycles = 96;
        private const int MmioReadHotSlotCount = 6;

        //Memory
        [NonSerialized] private unsafe byte* ramPtr = (byte*)Marshal.AllocHGlobal(2048 * 1024);
        [NonSerialized] private unsafe byte* ex1Ptr = (byte*)Marshal.AllocHGlobal(512 * 1024);
        [NonSerialized] private unsafe byte* scrathpadPtr = (byte*)Marshal.AllocHGlobal(1024);
        [NonSerialized] private unsafe byte* biosPtr = (byte*)Marshal.AllocHGlobal(512 * 1024);
        [NonSerialized] private unsafe byte* sio = (byte*)Marshal.AllocHGlobal(0x10);
        [NonSerialized] private unsafe byte* memoryControl1 = (byte*)Marshal.AllocHGlobal(0x40);
        [NonSerialized] private unsafe byte* memoryControl2 = (byte*)Marshal.AllocHGlobal(0x10);

        private uint memoryCache;
        private uint memoryCacheWriteCount;
        [NonSerialized] private Action<uint, int>? ramWriteObserver;
        [NonSerialized] private Action? memoryCacheWriteObserver;

        //Other Subsystems
        [NonSerialized] public InterruptController interruptController;
        [NonSerialized] private DMA dma;
        [NonSerialized] private GPU gpu;
        [NonSerialized] private CDROM cdrom;
        [NonSerialized] private TIMERS timers;
        [NonSerialized] private JOYPAD joypad;
        [NonSerialized] private MDEC mdec;
        [NonSerialized] private SPU spu;
        [NonSerialized] private Exp2 exp2;
        private int spuCycleAccumulator;
        private int _perfTickCalls;
        private int _perfTickCycles;
        private int _perfReadOpsFast;
        private int _perfReadOpsMmio;
        private int _perfWriteOpsFast;
        private int _perfWriteOpsMmio;
        private int _perfLoad32Ram;
        private int _perfLoad32Ex1;
        private int _perfLoad32Scratchpad;
        private int _perfLoad32Bios;
        private int _perfLoad32Mmio;
        private int _perfWrite32Ram;
        private int _perfWrite16Ram;
        private int _perfWrite8Ram;
        private int _perfWrite32Ex1;
        private int _perfWrite16Ex1;
        private int _perfWrite8Ex1;
        private int _perfWrite32Scratchpad;
        private int _perfWrite16Scratchpad;
        private int _perfWrite8Scratchpad;
        private int _perfWrite32Mmio;
        private int _perfWrite16Mmio;
        private int _perfWrite8Mmio;
        private int _perfRelaxedGpuStatusReads;
        private int _perfRelaxedJoyStatusReads;
        private int _perfRelaxedTimer2Reads;
        private int _perfRelaxedInterruptStatusReads;
        private int _perfMmioShadowHits;
        private int _cpuTightSyncBudgetCycles;
        private readonly uint[] _perfMmioReadHotAddr = new uint[MmioReadHotSlotCount];
        private readonly int[] _perfMmioReadHotCount = new int[MmioReadHotSlotCount];
        private int _mmioShadowEpoch;
        private int _gpuStatShadowEpoch = -1;
        private uint _gpuStatShadowValue;
        private int _interruptStatusShadowEpoch = -1;
        private uint _interruptStatusShadowValue;
        private int _joyStatShadowEpoch = -1;
        private uint _joyStatShadowValue;
        private int _timer2ValueShadowEpoch = -1;
        private uint _timer2ValueShadowValue;
        private int _timer2ModeShadowEpoch = -1;
        private uint _timer2ModeShadowValue;

        //temporary hardcoded bios/ex1
        private static string bios = "./SCPH1001.BIN"; //SCPH1001 //openbios
        private static string ex1 = "./caetlaEXP.BIN";

        public unsafe BUS(GPU gpu, CDROM cdrom, SPU spu, JOYPAD joypad, TIMERS timers, MDEC mdec, InterruptController interruptController, Exp2 exp2) {
            dma = new DMA(this);
            this.gpu = gpu;
            this.cdrom = cdrom;
            this.timers = timers;
            this.mdec = mdec;
            this.spu = spu;
            this.joypad = joypad;
            this.interruptController = interruptController;
            this.exp2 = exp2;
            ClearAllocatedMemory();
            write(0x4, Sio1StatusDefault, sio);
        }

        private unsafe void ClearAllocatedMemory() {
            new Span<byte>(ramPtr, 2048 * 1024).Clear();
            new Span<byte>(ex1Ptr, 512 * 1024).Clear();
            new Span<byte>(scrathpadPtr, 1024).Clear();
            new Span<byte>(biosPtr, 512 * 1024).Clear();
            new Span<byte>(sio, 0x10).Clear();
            new Span<byte>(memoryControl1, 0x40).Clear();
            new Span<byte>(memoryControl2, 0x10).Clear();
            memoryCache = 0;
            memoryCacheWriteCount = 0;
        }

        public void SetRamWriteObserver(Action<uint, int> observer) {
            ramWriteObserver = observer;
        }

        public void SetMemoryCacheWriteObserver(Action observer) {
            memoryCacheWriteObserver = observer;
        }

        public unsafe void SaveRawState(BinaryWriter writer) {
            writer.Write(new ReadOnlySpan<byte>(ramPtr, 2048 * 1024));
            writer.Write(new ReadOnlySpan<byte>(ex1Ptr, 512 * 1024));
            writer.Write(new ReadOnlySpan<byte>(scrathpadPtr, 1024));
            writer.Write(new ReadOnlySpan<byte>(biosPtr, 512 * 1024));
            writer.Write(new ReadOnlySpan<byte>(sio, 0x10));
            writer.Write(new ReadOnlySpan<byte>(memoryControl1, 0x40));
            writer.Write(new ReadOnlySpan<byte>(memoryControl2, 0x10));
        }

        public unsafe void LoadRawState(BinaryReader reader) {
            ReadExactly(reader, new Span<byte>(ramPtr, 2048 * 1024));
            ReadExactly(reader, new Span<byte>(ex1Ptr, 512 * 1024));
            ReadExactly(reader, new Span<byte>(scrathpadPtr, 1024));
            ReadExactly(reader, new Span<byte>(biosPtr, 512 * 1024));
            ReadExactly(reader, new Span<byte>(sio, 0x10));
            ReadExactly(reader, new Span<byte>(memoryControl1, 0x40));
            ReadExactly(reader, new Span<byte>(memoryControl2, 0x10));
        }

        private static void ReadExactly(BinaryReader reader, Span<byte> buffer) {
            while (!buffer.IsEmpty) {
                int read = reader.Read(buffer);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of stream while loading PSX BUS state.");

                buffer = buffer[read..];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint load32(uint address) {
            if (address == 0xFFFE0130) {
                NoteLoad32Mmio(address);
                NoteCpuMmioAccess();
                return memoryCache;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfReadOpsFast++;
                _perfLoad32Ram++;
                uint physical = addr & 0x1F_FFFF;
                uint value = load<uint>(physical, ramPtr);
                TraceRamRead(physical, 4, value);
                return value;
            } else if (addr < 0x1F80_0000) {
                _perfReadOpsFast++;
                _perfLoad32Ex1++;
                return load<uint>(addr & 0x7_FFFF, ex1Ptr);
            } else if (addr < 0x1f80_0400) {
                _perfReadOpsFast++;
                _perfLoad32Scratchpad++;
                return load<uint>(addr & 0x3FF, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return load<uint>(addr & 0xF, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                NoteLoad32Mmio(addr);
                if (addr == 0x1F80_1044 && TryLoadRelaxedJoyStatus(out uint joyStatus)) {
                    return joyStatus;
                }

                if (addr == 0x1F80_1040) {
                    InvalidateJoyStatusShadow();
                }

                NoteCpuMmioAccess();
                return joypad.load(addr);
            } else if (addr < 0x1F80_1060) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                if (addr == 0x1F80_1054) return Sio1StatusDefault;
                return load<uint>(addr & 0xF, sio);
            } else if (addr < 0x1F80_1070) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return load<uint>(addr & 0xF, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                NoteLoad32Mmio(addr);
                if (addr == 0x1F80_1070 && TryLoadRelaxedInterruptStatus(out uint interruptStatus)) {
                    return interruptStatus;
                }

                NoteCpuMmioAccess();
                return interruptController.load(addr);
            } else if (addr < 0x1F80_1100) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return dma.load(addr);
            } else if (addr < 0x1F80_1140) {
                NoteLoad32Mmio(addr);
                if (addr == 0x1F80_1120 && TryLoadRelaxedTimer2Value(out uint timer2Value)) {
                    return timer2Value;
                }

                if (addr == 0x1F80_1124 && TryLoadRelaxedTimer2Mode(out uint timer2Mode)) {
                    return timer2Mode;
                }

                NoteCpuMmioAccess();
                return timers.load(addr);
            } else if (addr <= 0x1F80_1803) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return cdrom.load(addr);
            } else if (addr == 0x1F80_1810) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return gpu.loadGPUREAD();
            } else if (addr == 0x1F80_1814) {
                NoteLoad32Mmio(addr);
                if (TryLoadRelaxedGpuStat(out uint gpuStatus)) {
                    return gpuStatus;
                }

                NoteCpuMmioAccess();
                return gpu.loadGPUSTAT();
            } else if (addr == 0x1F80_1820) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return mdec.readMDEC0_Data();
            } else if (addr == 0x1F80_1824) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return mdec.readMDEC1_Status();
            } else if (addr < 0x1F80_2000) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return spu.load(addr);
            } else if (addr < 0x1F80_4000) {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                return exp2.load(addr);
            } else if (addr < 0x1FC8_0000) {
                _perfReadOpsFast++;
                _perfLoad32Bios++;
                return load<uint>(addr & 0x7_FFFF, biosPtr);
            } else {
                NoteLoad32Mmio(addr);
                NoteCpuMmioAccess();
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Load32 Unsupported: {addr:x8} pc={CPU.TraceCurrentPC:x8}");
                return 0xFFFF_FFFF;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void write32(uint address, uint value) {
            if (address == 0xFFFE_0130) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                memoryCache = value;
                memoryCacheWriteCount++;
                memoryCacheWriteObserver?.Invoke();
                return;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfWriteOpsFast++;
                _perfWrite32Ram++;
                uint physical = addr & 0x1F_FFFF;
                TraceRamWrite(physical, 4, value, "cpu");
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 4);
            } else if (addr < 0x1F80_0000) {
                _perfWriteOpsFast++;
                _perfWrite32Ex1++;
                write(addr & 0x7_FFFF, value, ex1Ptr);
            } else if (addr < 0x1f80_0400) {
                _perfWriteOpsFast++;
                _perfWrite32Scratchpad++;
                write(addr & 0x3FF, value, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0x3F, value, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                joypad.write(addr, value);
            } else if (addr < 0x1F80_1060) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0xF, value, sio);
            } else if (addr < 0x1F80_1070) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0xF, value, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                interruptController.write(addr, value);
            } else if (addr < 0x1F80_1100) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                dma.write(addr, value);
            } else if (addr < 0x1F80_1140) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                timers.write(addr, value);
            } else if (addr < 0x1F80_1810) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                cdrom.write(addr, value);
            } else if (addr < 0x1F80_1820) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                gpu.write(addr, value);
            } else if (addr < 0x1F80_1830) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                mdec.write(addr, value);
            } else if (addr < 0x1F80_2000) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                spu.write(addr, (ushort)value);
            } else if (addr < 0x1F80_4000) {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                exp2.write(addr, value);
            } else {
                _perfWriteOpsMmio++;
                _perfWrite32Mmio++;
                NoteCpuMmioAccess();
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Write32 Unsupported: {addr:x8}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void write16(uint address, ushort value) {
            if (address == 0xFFFE_0130) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                memoryCache = value;
                memoryCacheWriteCount++;
                memoryCacheWriteObserver?.Invoke();
                return;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfWriteOpsFast++;
                _perfWrite16Ram++;
                uint physical = addr & 0x1F_FFFF;
                TraceRamWrite(physical, 2, value, "cpu");
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 2);
            } else if (addr < 0x1F80_0000) {
                _perfWriteOpsFast++;
                _perfWrite16Ex1++;
                write(addr & 0x7_FFFF, value, ex1Ptr);
            } else if (addr < 0x1F80_0400) {
                _perfWriteOpsFast++;
                _perfWrite16Scratchpad++;
                write(addr & 0x3FF, value, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0x3F, value, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                joypad.write(addr, value);
            } else if (addr < 0x1F80_1060) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0xF, value, sio);
            } else if (addr < 0x1F80_1070) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0xF, value, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                interruptController.write(addr, value);
            } else if (addr < 0x1F80_1100) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                dma.write(addr, value);
            } else if (addr < 0x1F80_1140) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                timers.write(addr, value);
            } else if (addr < 0x1F80_1810) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                cdrom.write(addr, value);
            } else if (addr < 0x1F80_1820) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                gpu.write(addr, value);
            } else if (addr < 0x1F80_1830) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                mdec.write(addr, value);
            } else if (addr < 0x1F80_2000) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                spu.write(addr, value);
            } else if (addr < 0x1F80_4000) {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                exp2.write(addr, value);
            } else {
                _perfWriteOpsMmio++;
                _perfWrite16Mmio++;
                NoteCpuMmioAccess();
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Write16 Unsupported: {addr:x8}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void write8(uint address, byte value) {
            if (address == 0xFFFE_0130) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                memoryCache = value;
                memoryCacheWriteCount++;
                memoryCacheWriteObserver?.Invoke();
                return;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfWriteOpsFast++;
                _perfWrite8Ram++;
                uint physical = addr & 0x1F_FFFF;
                TraceRamWrite(physical, 1, value, "cpu");
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 1);
            } else if (addr < 0x1F80_0000) {
                _perfWriteOpsFast++;
                _perfWrite8Ex1++;
                write(addr & 0x7_FFFF, value, ex1Ptr);
            } else if (addr < 0x1f80_0400) {
                _perfWriteOpsFast++;
                _perfWrite8Scratchpad++;
                write(addr & 0x3FF, value, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0x3F, value, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                joypad.write(addr, value);
            } else if (addr < 0x1F80_1060) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0xF, value, sio);
            } else if (addr < 0x1F80_1070) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                write(addr & 0xF, value, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                interruptController.write(addr, value);
            } else if (addr < 0x1F80_1100) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                dma.write(addr, value);
            } else if (addr < 0x1F80_1140) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                timers.write(addr, value);
            } else if (addr < 0x1F80_1810) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                cdrom.write(addr, value);
            } else if (addr < 0x1F80_1820) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                InvalidateMmioReadShadows();
                gpu.write(addr, value);
            } else if (addr < 0x1F80_1830) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                mdec.write(addr, value);
            } else if (addr < 0x1F80_2000) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                spu.write(addr, value);
            } else if (addr < 0x1F80_4000) {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                exp2.write(addr, value);
            } else {
                _perfWriteOpsMmio++;
                _perfWrite8Mmio++;
                NoteCpuMmioAccess();
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Write8 Unsupported: {addr:x8}");
            }
        }

        internal unsafe bool loadBios() {
            if (TryResolveBiosPath(out string? foundBiosPath, out string[] attemptedPaths) == false || string.IsNullOrWhiteSpace(foundBiosPath)) {
                Console.WriteLine("[BUS] No BIOS file found. Tried locations:");
                foreach (string path in attemptedPaths) {
                    Console.WriteLine($"  {path}");
                }
                return false;
            }

            try {
                byte[] rom = VirtualFileSystem.ReadAllBytes(foundBiosPath);
                if (rom.Length < 512 * 1024) {
                    Console.WriteLine($"[BUS] BIOS file is too small: {foundBiosPath} ({rom.Length} bytes)");
                    return false;
                }

                Marshal.Copy(rom, 0, (IntPtr)biosPtr, Math.Min(rom.Length, 512 * 1024));
                Console.WriteLine($"[BUS] BIOS File found at: {foundBiosPath}");
                Console.WriteLine("[BUS] BIOS Contents Loaded.");
                return true;
            } catch (Exception e) {
                Console.WriteLine($"[BUS] Error loading BIOS from {foundBiosPath}:\n" + e.Message);
                return false;
            }
        }

        internal static bool TryResolveBiosPath(out string? foundBiosPath, out string[] attemptedPaths) {
            var attempts = new System.Collections.Generic.List<string>();
            string? overridePath = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BIOS");
            if (!string.IsNullOrWhiteSpace(overridePath)) {
                attempts.Add(overridePath);
                if (VirtualFileSystem.Exists(overridePath)) {
                    foundBiosPath = overridePath;
                    attemptedPaths = attempts.ToArray();
                    return true;
                }
            }

            string[] biosFilenames = { "SCPH1001.BIN", "scph1001.bin", "SCPH1001.bin", "scph1001.BIN" };
            string currentDirectory = ".";
            string appBaseDirectory = ".";
            try {
                currentDirectory = Environment.CurrentDirectory;
            } catch {
            }
            try {
                appBaseDirectory = AppContext.BaseDirectory;
            } catch {
            }

            string[] biosPaths = {
                "./{0}",
                "{0}",
                "../bios/{0}",
                "../../bios/{0}",
                "../../../bios/{0}",
                "/bios/{0}",
                Path.Combine(currentDirectory, "bios", "{0}"),
                Path.Combine(appBaseDirectory, "bios", "{0}")
            };

            foreach (string filename in biosFilenames) {
                foreach (string pathTemplate in biosPaths) {
                    string path = string.Format(pathTemplate, filename);
                    attempts.Add(path);
                    if (VirtualFileSystem.Exists(path)) {
                        foundBiosPath = path;
                        attemptedPaths = attempts.ToArray();
                        return true;
                    }
                }
            }

            foundBiosPath = null;
            attemptedPaths = attempts.ToArray();
            return false;
        }

        //PSX executables are having an 800h-byte header, followed by the code/data.
        //
        // 000h-007h ASCII ID "PS-x EXE"
        // 008h-00Fh Zerofilled
        // 010h Initial PC(usually 80010000h, or higher)
        // 014h Initial GP/R28(usually 0)
        // 018h Destination Address in RAM(usually 80010000h, or higher)
        // 01Ch Filesize(must be N*800h)    (excluding 800h-byte header)
        // 020h Unknown/Unused(usually 0)
        // 024h Unknown/Unused(usually 0)
        // 028h Memfill Start Address(usually 0) (when below Size = None)
        // 02Ch Memfill Size in bytes(usually 0) (0=None)
        // 030h Initial SP/R29 & FP/R30 Base(usually 801FFFF0h) (or 0=None)
        // 034h Initial SP/R29 & FP/R30 Offs(usually 0, added to above Base)
        // 038h-04Bh Reserved for A(43h) Function(should be zerofilled in exefile)
        // 04Ch-xxxh ASCII marker
        //            "Sony Computer Entertainment Inc. for Japan area"
        //            "Sony Computer Entertainment Inc. for Europe area"
        //            "Sony Computer Entertainment Inc. for North America area"
        //            (or often zerofilled in some homebrew files)
        //            (the BIOS doesn't verify this string, and boots fine without it)
        // xxxh-7FFh Zerofilled
        // 800h...   Code/Data(loaded to entry[018h] and up)

        public unsafe void loadEXE(String fileName) {
            byte[] exe = File.ReadAllBytes(fileName);
            loadEXE(exe, fileName);
        }

        public unsafe void loadEXE(byte[] exe, string sourceLabel) {
            uint PC = Unsafe.As<byte, uint>(ref exe[0x10]);
            uint R28 = Unsafe.As<byte, uint>(ref exe[0x14]);
            uint R29 = Unsafe.As<byte, uint>(ref exe[0x30]);
            uint R30 = R29; //base
            R30 += Unsafe.As<byte, uint>(ref exe[0x34]); //offset

            uint DestAdress = Unsafe.As<byte, uint>(ref exe[0x18]);

            Console.WriteLine($"SideLoading PSX EXE ({sourceLabel}): PC {PC:x8} R28 {R28:x8} R29 {R29:x8} R30 {R30:x8}");

            uint physicalDest = DestAdress & 0x1F_FFFF;
            int copyLength = exe.Length - 0x800;
            Marshal.Copy(exe, 0x800, (IntPtr)(ramPtr + physicalDest), copyLength);
            ramWriteObserver?.Invoke(physicalDest, copyLength);

            // Patch Bios LoadRunShell() at 0xBFC06FF0 before the jump to 0x80030000 so we don't poll the address every cycle
            // Instructions are LUI and ORI duos that load to the specified register but PC that loads to R8/Temp0
            // The last 2 instr are a JR to R8 and a NOP.
            write(0x6FF0 +  0, 0x3C080000 | PC >> 16, biosPtr);
            write(0x6FF0 +  4, 0x35080000 | PC & 0xFFFF, biosPtr);

            write(0x6FF0 +  8, 0x3C1C0000 | R28 >> 16, biosPtr);
            write(0x6FF0 + 12, 0x379C0000 | R28 & 0xFFFF, biosPtr);

            if (R29 != 0) {
                write(0x6FF0 + 16, 0x3C1D0000 | R29 >> 16, biosPtr);
                write(0x6FF0 + 20, 0x37BD0000 | R29 & 0xFFFF, biosPtr);

                write(0x6FF0 + 24, 0x3C1E0000 | R30 >> 16, biosPtr);
                write(0x6FF0 + 28, 0x37DE0000 | R30 & 0xFFFF, biosPtr);

                write(0x6FF0 + 32, 0x01000008, biosPtr);
                write(0x6FF0 + 36, 0x00000000, biosPtr);
            } else {
                write(0x6FF0 + 16, 0x01000008, biosPtr);
                write(0x6FF0 + 20, 0x00000000, biosPtr);
            }
        }

        public unsafe void loadEXP() {
            // Try multiple locations for EXP file
            // Try both uppercase and lowercase filenames since Linux is case-sensitive
            string[] expFilenames = { "caetlaEXP.BIN", "caetlaexp.bin", "CAETLAEXP.BIN", "caetlaexp.BIN" };
            string[] expPaths = {
                "./{0}",                    // Current directory
                "{0}",                      // Current directory (no ./)
                "../bios/{0}",              // ../bios directory
                "../../bios/{0}",           // ../../bios directory
                "../../../bios/{0}",        // ../../../bios directory
                "/bios/{0}",                // Absolute /bios directory
                Path.Combine(Environment.CurrentDirectory, "bios", "{0}"), // Current dir/bios
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bios", "{0}") // App base/bios
            };
            
            string foundExpPath = null;
            foreach (string filename in expFilenames) {
                foreach (string pathTemplate in expPaths) {
                    string path = string.Format(pathTemplate, filename);
                    if (File.Exists(path)) {
                        foundExpPath = path;
                        break;
                    }
                }
                if (foundExpPath != null) break;
            }
            
            if (foundExpPath == null) {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[BUS] No EXP file (caetlaEXP.BIN) found. Tried locations:");
                foreach (string path in expPaths) {
                    Console.WriteLine($"  {path}");
                }
                Console.ResetColor();
                return;
            }
            
            try {
                byte[] exe = File.ReadAllBytes(foundExpPath);
                Marshal.Copy(exe, 0, (IntPtr)ex1Ptr, exe.Length);

                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[BUS] EXP File found at: {foundExpPath}");
                Console.WriteLine("[BUS] EXP Contents Loaded.");
                Console.ResetColor();
                
                write32(0x1F02_0018, 0x1); //Enable exp flag
            } catch (Exception e) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[BUS] Error loading EXP from {foundExpPath}:\n" + e.Message);
                Console.ResetColor();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void tick(int cycles) {
            InvalidateMmioReadShadows();
            if (_cpuTightSyncBudgetCycles > 0) {
                _cpuTightSyncBudgetCycles -= cycles;
                if (_cpuTightSyncBudgetCycles < 0) {
                    _cpuTightSyncBudgetCycles = 0;
                }
            }
            _perfTickCalls++;
            _perfTickCycles += cycles;
            if (gpu.tick(cycles)) interruptController.set(Interrupt.VBLANK);
            if (cdrom.HasPendingWork && cdrom.tick(cycles)) interruptController.set(Interrupt.CDROM);
            if (dma.HasPendingWork && dma.tick(cycles)) interruptController.set(Interrupt.DMA);

            uint timerInterrupts = timers.tickAll(gpu.getBlanksAndDot(), cycles);
            if ((timerInterrupts & 0x1) != 0) interruptController.set(Interrupt.TIMER0);
            if ((timerInterrupts & 0x2) != 0) interruptController.set(Interrupt.TIMER1);
            if ((timerInterrupts & 0x4) != 0) interruptController.set(Interrupt.TIMER2);
            if (joypad.HasPendingWork && joypad.tick(cycles)) interruptController.set(Interrupt.CONTR);

            spuCycleAccumulator += cycles;
            if (spuCycleAccumulator >= SpuTickBatchCycles) {
                if (spu.tick(spuCycleAccumulator)) interruptController.set(Interrupt.SPU);
                spuCycleAccumulator = 0;
            }
        }

        public void ResetPerfCounters() {
            _perfTickCalls = 0;
            _perfTickCycles = 0;
            _perfReadOpsFast = 0;
            _perfReadOpsMmio = 0;
            _perfWriteOpsFast = 0;
            _perfWriteOpsMmio = 0;
            _perfLoad32Ram = 0;
            _perfLoad32Ex1 = 0;
            _perfLoad32Scratchpad = 0;
            _perfLoad32Bios = 0;
            _perfLoad32Mmio = 0;
            _perfWrite32Ram = 0;
            _perfWrite16Ram = 0;
            _perfWrite8Ram = 0;
            _perfWrite32Ex1 = 0;
            _perfWrite16Ex1 = 0;
            _perfWrite8Ex1 = 0;
            _perfWrite32Scratchpad = 0;
            _perfWrite16Scratchpad = 0;
            _perfWrite8Scratchpad = 0;
            _perfWrite32Mmio = 0;
            _perfWrite16Mmio = 0;
            _perfWrite8Mmio = 0;
            _perfRelaxedGpuStatusReads = 0;
            _perfRelaxedJoyStatusReads = 0;
            _perfRelaxedTimer2Reads = 0;
            _perfRelaxedInterruptStatusReads = 0;
            _perfMmioShadowHits = 0;
            Array.Clear(_perfMmioReadHotAddr, 0, _perfMmioReadHotAddr.Length);
            Array.Clear(_perfMmioReadHotCount, 0, _perfMmioReadHotCount.Length);
        }

        public PerfSnapshot CapturePerfSnapshot() {
            CaptureTopMmioReadEntries(
                out uint topMmioReadAddr0,
                out int topMmioReadCount0,
                out uint topMmioReadAddr1,
                out int topMmioReadCount1,
                out uint topMmioReadAddr2,
                out int topMmioReadCount2);

            return new PerfSnapshot(
                _perfTickCalls,
                _perfTickCycles,
                _perfReadOpsFast,
                _perfReadOpsMmio,
                _perfWriteOpsFast,
                _perfWriteOpsMmio,
                _perfLoad32Ram,
                _perfLoad32Ex1,
                _perfLoad32Scratchpad,
                _perfLoad32Bios,
                _perfLoad32Mmio,
                _perfWrite32Ram,
                _perfWrite16Ram,
                _perfWrite8Ram,
                _perfWrite32Ex1,
                _perfWrite16Ex1,
                _perfWrite8Ex1,
                _perfWrite32Scratchpad,
                _perfWrite16Scratchpad,
                _perfWrite8Scratchpad,
                _perfWrite32Mmio,
                _perfWrite16Mmio,
                _perfWrite8Mmio,
                topMmioReadAddr0,
                topMmioReadCount0,
                topMmioReadAddr1,
                topMmioReadCount1,
                topMmioReadAddr2,
                topMmioReadCount2,
                _perfRelaxedGpuStatusReads,
                _perfRelaxedJoyStatusReads,
                _perfRelaxedTimer2Reads,
                _perfRelaxedInterruptStatusReads,
                _perfMmioShadowHits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe T load<T>(uint addr, byte* ptr) where T : unmanaged {
            return Unsafe.ReadUnaligned<T>(ptr + addr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void write<T>(uint addr, T value, byte* ptr) where T : unmanaged {
            Unsafe.WriteUnaligned(ptr + addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NoteCpuMmioAccess() {
            if (_cpuTightSyncBudgetCycles < CpuTightSyncBudgetCycles) {
                _cpuTightSyncBudgetCycles = CpuTightSyncBudgetCycles;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NoteLoad32Mmio(uint addr) {
            _perfReadOpsMmio++;
            _perfLoad32Mmio++;
            NotePerfMmioReadAddress(addr);
        }

        private void NotePerfMmioReadAddress(uint addr) {
            for (int i = 0; i < MmioReadHotSlotCount; i++) {
                if (_perfMmioReadHotCount[i] == 0) {
                    _perfMmioReadHotAddr[i] = addr;
                    _perfMmioReadHotCount[i] = 1;
                    return;
                }

                if (_perfMmioReadHotAddr[i] == addr) {
                    _perfMmioReadHotCount[i]++;
                    return;
                }
            }

            int weakestIndex = 0;
            for (int i = 1; i < MmioReadHotSlotCount; i++) {
                if (_perfMmioReadHotCount[i] < _perfMmioReadHotCount[weakestIndex]) {
                    weakestIndex = i;
                }
            }

            if (_perfMmioReadHotCount[weakestIndex] > 1) {
                _perfMmioReadHotCount[weakestIndex]--;
                return;
            }

            _perfMmioReadHotAddr[weakestIndex] = addr;
            _perfMmioReadHotCount[weakestIndex] = 1;
        }

        private void CaptureTopMmioReadEntries(
            out uint addr0,
            out int count0,
            out uint addr1,
            out int count1,
            out uint addr2,
            out int count2) {
            addr0 = 0;
            count0 = 0;
            addr1 = 0;
            count1 = 0;
            addr2 = 0;
            count2 = 0;

            for (int i = 0; i < MmioReadHotSlotCount; i++) {
                uint addr = _perfMmioReadHotAddr[i];
                int count = _perfMmioReadHotCount[i];
                if (count <= 0) {
                    continue;
                }

                if (count > count0) {
                    addr2 = addr1;
                    count2 = count1;
                    addr1 = addr0;
                    count1 = count0;
                    addr0 = addr;
                    count0 = count;
                } else if (count > count1) {
                    addr2 = addr1;
                    count2 = count1;
                    addr1 = addr;
                    count1 = count;
                } else if (count > count2) {
                    addr2 = addr;
                    count2 = count;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvalidateMmioReadShadows() {
            _mmioShadowEpoch++;
            _gpuStatShadowEpoch = -1;
            _interruptStatusShadowEpoch = -1;
            _joyStatShadowEpoch = -1;
            _timer2ValueShadowEpoch = -1;
            _timer2ModeShadowEpoch = -1;
        }

        private bool TryLoadRelaxedInterruptStatus(out uint value) {
            _perfRelaxedInterruptStatusReads++;
            if (_interruptStatusShadowEpoch == _mmioShadowEpoch) {
                _perfMmioShadowHits++;
                value = _interruptStatusShadowValue;
                return true;
            }

            _interruptStatusShadowValue = interruptController.load(0x1F80_1070);
            _interruptStatusShadowEpoch = _mmioShadowEpoch;
            value = _interruptStatusShadowValue;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvalidateJoyStatusShadow() {
            _joyStatShadowEpoch = -1;
        }

        private bool TryLoadRelaxedGpuStat(out uint value) {
            if (!RelaxGpuStatusPolling || !gpu.CanRelaxStatusPolling()) {
                value = 0;
                return false;
            }

            _perfRelaxedGpuStatusReads++;
            if (_gpuStatShadowEpoch == _mmioShadowEpoch) {
                _perfMmioShadowHits++;
                value = _gpuStatShadowValue;
                return true;
            }

            _gpuStatShadowValue = gpu.loadGPUSTAT();
            _gpuStatShadowEpoch = _mmioShadowEpoch;
            value = _gpuStatShadowValue;
            return true;
        }

        private bool TryLoadRelaxedJoyStatus(out uint value) {
            if (!RelaxJoyStatusPolling || !joypad.CanRelaxStatusPolling()) {
                value = 0;
                return false;
            }

            _perfRelaxedJoyStatusReads++;
            if (_joyStatShadowEpoch == _mmioShadowEpoch) {
                _perfMmioShadowHits++;
                value = _joyStatShadowValue;
                return true;
            }

            _joyStatShadowValue = joypad.load(0x1F80_1044);
            _joyStatShadowEpoch = _mmioShadowEpoch;
            value = _joyStatShadowValue;
            return true;
        }

        private bool TryLoadRelaxedTimer2Value(out uint value) {
            if (!RelaxTimer2Polling || !timers.CanRelaxPolling(0x1F80_1120)) {
                value = 0;
                return false;
            }

            _perfRelaxedTimer2Reads++;
            if (_timer2ValueShadowEpoch == _mmioShadowEpoch) {
                _perfMmioShadowHits++;
                value = _timer2ValueShadowValue;
                return true;
            }

            _timer2ValueShadowValue = timers.load(0x1F80_1120);
            _timer2ValueShadowEpoch = _mmioShadowEpoch;
            value = _timer2ValueShadowValue;
            return true;
        }

        private bool TryLoadRelaxedTimer2Mode(out uint value) {
            if (!RelaxTimer2Polling || !timers.CanRelaxPolling(0x1F80_1124)) {
                value = 0;
                return false;
            }

            _perfRelaxedTimer2Reads++;
            if (_timer2ModeShadowEpoch == _mmioShadowEpoch) {
                _perfMmioShadowHits++;
                value = _timer2ModeShadowValue;
                return true;
            }

            _timer2ModeShadowValue = timers.load(0x1F80_1124);
            _timer2ModeShadowEpoch = _mmioShadowEpoch;
            value = _timer2ModeShadowValue;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryLoad8Fast(uint address, out byte value) {
            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfReadOpsFast++;
                uint physical = addr & 0x1F_FFFF;
                value = *(ramPtr + physical);
                TraceRamRead(physical, 1, value);
                return true;
            }

            if (addr < 0x1F80_0000) {
                _perfReadOpsFast++;
                value = *(ex1Ptr + (addr & 0x7_FFFF));
                return true;
            }

            if (addr < 0x1F80_0400) {
                _perfReadOpsFast++;
                value = *(scrathpadPtr + (addr & 0x3FF));
                return true;
            }

            if (addr < 0x1FC8_0000 && addr >= 0x1FC0_0000) {
                _perfReadOpsFast++;
                value = *(biosPtr + (addr & 0x7_FFFF));
                return true;
            }

            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryLoad16Fast(uint address, out ushort value) {
            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfReadOpsFast++;
                uint physical = addr & 0x1F_FFFF;
                value = Unsafe.ReadUnaligned<ushort>(ramPtr + physical);
                TraceRamRead(physical, 2, value);
                return true;
            }

            if (addr < 0x1F80_0000) {
                _perfReadOpsFast++;
                value = Unsafe.ReadUnaligned<ushort>(ex1Ptr + (addr & 0x7_FFFF));
                return true;
            }

            if (addr < 0x1F80_0400) {
                _perfReadOpsFast++;
                value = Unsafe.ReadUnaligned<ushort>(scrathpadPtr + (addr & 0x3FF));
                return true;
            }

            if (addr < 0x1FC8_0000 && addr >= 0x1FC0_0000) {
                _perfReadOpsFast++;
                value = Unsafe.ReadUnaligned<ushort>(biosPtr + (addr & 0x7_FFFF));
                return true;
            }

            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryLoad32Fast(uint address, out uint value) {
            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfReadOpsFast++;
                _perfLoad32Ram++;
                uint physical = addr & 0x1F_FFFF;
                value = Unsafe.ReadUnaligned<uint>(ramPtr + physical);
                TraceRamRead(physical, 4, value);
                return true;
            }

            if (addr < 0x1F80_0000) {
                _perfReadOpsFast++;
                _perfLoad32Ex1++;
                value = Unsafe.ReadUnaligned<uint>(ex1Ptr + (addr & 0x7_FFFF));
                return true;
            }

            if (addr < 0x1F80_0400) {
                _perfReadOpsFast++;
                _perfLoad32Scratchpad++;
                value = Unsafe.ReadUnaligned<uint>(scrathpadPtr + (addr & 0x3FF));
                return true;
            }

            if (addr < 0x1FC8_0000 && addr >= 0x1FC0_0000) {
                _perfReadOpsFast++;
                _perfLoad32Bios++;
                value = Unsafe.ReadUnaligned<uint>(biosPtr + (addr & 0x7_FFFF));
                return true;
            }

            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryStore32Fast(uint address, uint value) {
            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfWriteOpsFast++;
                _perfWrite32Ram++;
                uint physical = addr & 0x1F_FFFF;
                TraceRamWrite(physical, 4, value, "cpu");
                Unsafe.WriteUnaligned(ramPtr + physical, value);
                ramWriteObserver?.Invoke(physical, 4);
                return true;
            }

            if (addr < 0x1F80_0000) {
                _perfWriteOpsFast++;
                _perfWrite32Ex1++;
                Unsafe.WriteUnaligned(ex1Ptr + (addr & 0x7_FFFF), value);
                return true;
            }

            if (addr < 0x1F80_0400) {
                _perfWriteOpsFast++;
                _perfWrite32Scratchpad++;
                Unsafe.WriteUnaligned(scrathpadPtr + (addr & 0x3FF), value);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryStore16Fast(uint address, ushort value) {
            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfWriteOpsFast++;
                _perfWrite16Ram++;
                uint physical = addr & 0x1F_FFFF;
                TraceRamWrite(physical, 2, value, "cpu");
                Unsafe.WriteUnaligned(ramPtr + physical, value);
                ramWriteObserver?.Invoke(physical, 2);
                return true;
            }

            if (addr < 0x1F80_0000) {
                _perfWriteOpsFast++;
                _perfWrite16Ex1++;
                Unsafe.WriteUnaligned(ex1Ptr + (addr & 0x7_FFFF), value);
                return true;
            }

            if (addr < 0x1F80_0400) {
                _perfWriteOpsFast++;
                _perfWrite16Scratchpad++;
                Unsafe.WriteUnaligned(scrathpadPtr + (addr & 0x3FF), value);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryStore8Fast(uint address, byte value) {
            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            if (addr < 0x1F00_0000) {
                _perfWriteOpsFast++;
                _perfWrite8Ram++;
                uint physical = addr & 0x1F_FFFF;
                TraceRamWrite(physical, 1, value, "cpu");
                *(ramPtr + physical) = value;
                ramWriteObserver?.Invoke(physical, 1);
                return true;
            }

            if (addr < 0x1F80_0000) {
                _perfWriteOpsFast++;
                _perfWrite8Ex1++;
                *(ex1Ptr + (addr & 0x7_FFFF)) = value;
                return true;
            }

            if (addr < 0x1F80_0400) {
                _perfWriteOpsFast++;
                _perfWrite8Scratchpad++;
                *(scrathpadPtr + (addr & 0x3FF)) = value;
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint LoadFromRam(uint addr) {
            return *(uint*)(ramPtr + (addr & 0x1F_FFFF));
        }

        public DMA DMAController => dma;
        public bool RequiresFrequentSync(int deferredCycles)
            => _cpuTightSyncBudgetCycles > 0
                || cdrom.RequiresFrequentSync
                || dma.HasPendingWork
                || joypad.RequiresFrequentSync(deferredCycles);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint LoadFromBios(uint addr) {
            return *(uint*)(biosPtr + (addr & 0x7_FFFF));
        }

        public uint MemoryCacheControl => memoryCache;

        public uint MemoryCacheWriteCount => memoryCacheWriteCount;
        public DMA Dma => dma;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<uint> DmaFromRam(uint addr, uint size) {
            uint physical = addr & 0x1F_FFFF;
            TraceRamDmaRead(physical, size);
            return new Span<uint>(ramPtr + physical, (int)size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaToRam(uint addr, uint value) {
            uint physical = addr & 0x1F_FFFF;
            TraceRamWrite(physical, 4, value, "dma");
            *(uint*)(ramPtr + physical) = value;
            ramWriteObserver?.Invoke(physical, 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaToRam(uint addr, byte[] buffer, uint size) {
            uint physical = addr & 0x1F_FFFF;
            int byteCount = (int)size * 4;
            if (ShouldTraceRamWriteRange(physical, (uint)byteCount)) {
                int previewCount = Math.Min(byteCount, 16);
                string preview = BitConverter.ToString(buffer, 0, previewCount);
                Console.WriteLine(
                    $"[PSX-RAM-WRITE] src=dma-bulk addr={physical:x6} bytes={byteCount} preview={preview} pc={CPU.TraceCurrentPC:x8}");
            }
            Marshal.Copy(buffer, 0, (IntPtr)(ramPtr + physical), byteCount);
            ramWriteObserver?.Invoke(physical, byteCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DmaFromGpu(uint address, int size) { //todo handle the whole array/span
            for (int i = 0; i < size; i++) {
                var word = gpu.loadGPUREAD();
                DmaToRam(address, word);
                address += 4;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DmaToGpu(Span<uint> buffer) {
            gpu.processDma(buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaFromCD(uint address, int size) {
            var dma = cdrom.processDmaLoad(size);
            uint physical = address & 0x1F_FFFC;
            if (TraceCdDma || ShouldTraceRamWriteRange(physical, (uint)(dma.Length * 4))) {
                int previewCount = Math.Min(dma.Length, 4);
                string preview = string.Empty;
                for (int i = 0; i < previewCount; i++) {
                    if (i != 0) {
                        preview += "-";
                    }
                    preview += dma[i].ToString("x8");
                }
                Console.WriteLine(
                    $"[PSX-RAM-WRITE] src=cd-dma addr={physical:x6} words={dma.Length} preview={preview} pc={CPU.TraceCurrentPC:x8}");
            }
            var dest = new Span<uint>(ramPtr + physical, dma.Length);
            dma.CopyTo(dest);
            ramWriteObserver?.Invoke(physical, dma.Length * 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetCdDmaWordCount() => cdrom.GetDmaWordCount();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DmaToMdecIn(Span<uint> dma) { //todo: actual process the whole array
            foreach (uint word in dma)
                mdec.writeMDEC0_Command(word);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaFromMdecOut(uint address, int size) {
            var dma = mdec.processDmaLoad(size);
            uint physical = address & 0x1F_FFFC;
            var dest = new Span<uint>(ramPtr + physical, size);
            dma.CopyTo(dest);
            ramWriteObserver?.Invoke(physical, dma.Length * 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe int DmaFromMdecOutPartial(uint address, int size) {
            var dma = mdec.processDmaLoad(size);
            if (dma.Length == 0) {
                return 0;
            }

            uint physical = address & 0x1F_FFFC;
            var dest = new Span<uint>(ramPtr + physical, dma.Length);
            dma.CopyTo(dest);
            ramWriteObserver?.Invoke(physical, dma.Length * 4);
            return dma.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanDmaFromMdecOut(int size) {
            return mdec.canDmaLoad(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanDmaToMdecIn(int size) {
            return mdec.canDmaStore(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DmaToSpu(Span<uint> dma) {
            spu.processDmaWrite(dma);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSpuDmaWriteWordCapacity() => spu.GetDmaWriteWordCapacity();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSpuDmaReadWordAvailability() => spu.GetDmaReadWordAvailability();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DmaToSpuPartial(Span<uint> dma) {
            return spu.processDmaWritePartial(dma);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaFromSpu(uint address, int size) {
            var dma = spu.processDmaLoad(size);
            uint physical = address & 0x1F_FFFC;
            var dest = new Span<uint>(ramPtr + physical, size);
            dma.CopyTo(dest);
            ramWriteObserver?.Invoke(physical, size * 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe int DmaFromSpuPartial(uint address, int size) {
            uint physical = address & 0x1F_FFFC;
            var dest = new Span<uint>(ramPtr + physical, size);
            int transferred = spu.processDmaLoadPartial(dest);
            if (transferred > 0) {
                ramWriteObserver?.Invoke(physical, transferred * 4);
            }
            return transferred;
        }
        public unsafe void DmaOTC(uint baseAddress, int size) {
            //uint destAddress = (uint)(baseAddress - ((size - 1) * 4));
            //
            //Span<uint> dma = stackalloc uint[size];
            //
            //for (int i = dma.Length - 1; i > 0; i--) {
            //baseAddress -= 4;
            //dma[i] = baseAddress & 0xFF_FFFF;
            //}
            //
            //dma[0] = 0xFF_FFFF;
            //
            //var dest = new Span<uint>(ramPtr + (destAddress & 0x1F_FFFC), size);
            //dma.CopyTo(dest);

            for (int i = 0; i < size - 1; i++) {
                DmaToRam(baseAddress, baseAddress - 4);
                baseAddress -= 4;
            }

            DmaToRam(baseAddress, 0xFF_FFFF);
        }

        private static void TraceRamWrite(uint physicalAddress, int sizeBytes, uint value, string source) {
            if (!ShouldTraceRamWriteRange(physicalAddress, (uint)sizeBytes)) {
                return;
            }

            Console.WriteLine(
                $"[PSX-RAM-WRITE] src={source} addr={physicalAddress:x6} size={sizeBytes} value={value:x8} pc={CPU.TraceCurrentPC:x8}");
        }

        private static void TraceRamRead(uint physicalAddress, int sizeBytes, uint value) {
            if (!ShouldTraceRamReadRange(physicalAddress, (uint)sizeBytes)) {
                return;
            }

            Console.WriteLine(
                $"[PSX-RAM-READ] addr={physicalAddress:x6} size={sizeBytes} value={value:x8} pc={CPU.TraceCurrentPC:x8}");
        }

        private unsafe void TraceRamDmaRead(uint physicalAddress, uint sizeWords) {
            uint sizeBytes = sizeWords * 4;
            if (!ShouldTraceRamReadRange(physicalAddress, sizeBytes)) {
                return;
            }

            int previewCount = (int)Math.Min(sizeWords, 4);
            string preview = string.Empty;
            for (int i = 0; i < previewCount; i++) {
                if (i != 0) {
                    preview += "-";
                }
                preview += (*(uint*)(ramPtr + physicalAddress + (uint)(i * 4))).ToString("x8");
            }

            Console.WriteLine(
                $"[PSX-RAM-READ] src=dma addr={physicalAddress:x6} words={sizeWords} preview={preview} pc={CPU.TraceCurrentPC:x8}");
        }

        private static bool ShouldTraceRamReadRange(uint physicalAddress, uint sizeBytes) {
            if (!TraceRamReadStart.HasValue || !TraceRamReadEnd.HasValue) {
                return false;
            }

            if (s_traceRamReadCount >= TraceRamReadLimit) {
                return false;
            }

            uint start = TraceRamReadStart.Value;
            uint end = TraceRamReadEnd.Value;
            uint readEnd = sizeBytes == 0 ? physicalAddress : physicalAddress + sizeBytes - 1;
            bool overlaps = !(readEnd < start || physicalAddress > end);
            if (overlaps) {
                s_traceRamReadCount++;
            }
            return overlaps;
        }

        private static bool ShouldTraceRamWriteRange(uint physicalAddress, uint sizeBytes) {
            if (!TraceRamWriteStart.HasValue || !TraceRamWriteEnd.HasValue) {
                return false;
            }

            if (s_traceRamWriteCount >= TraceRamWriteLimit) {
                return false;
            }

            uint start = TraceRamWriteStart.Value;
            uint end = TraceRamWriteEnd.Value;
            uint writeEnd = sizeBytes == 0 ? physicalAddress : physicalAddress + sizeBytes - 1;
            bool overlaps = !(writeEnd < start || physicalAddress > end);
            if (overlaps) {
                s_traceRamWriteCount++;
            }
            return overlaps;
        }

        private static uint? ParseOptionalHexEnv(string name) {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) {
                return null;
            }

            string token = raw.Trim();
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                token = token[2..];
            }

            return uint.TryParse(token, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value)
                ? value
                : null;
        }

        private static int ParseOptionalPositiveInt(string name, int fallback) {
            string? raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out int value) && value > 0
                ? value
                : fallback;
        }

        private static uint[] RegionMask = {
            0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, // KUSEG: 2048MB
            0x7FFF_FFFF,                                        // KSEG0:  512MB
            0x1FFF_FFFF,                                        // KSEG1:  512MB
            0x1FFF_FFFF, 0x1FFF_FFFF,                           // KSEG2: 1024MB (PSX-style top-bit aliasing except cache control)
        };
    }
}
