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
        private const uint Sio1StatusDefault = 0x0000_0805;
        private const uint MemoryCacheControlAddress = 0xFFFE_0130;
        private const uint MmioStartAddress = 0x1F80_0400;
        private const uint MmioEndAddressExclusive = 0x1FC0_0000;
        private const int RecentMmioAccessHysteresisCycles = 96;
        private static readonly bool VerboseBusAccess = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1";
        private static readonly uint? TraceRamReadStart = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_READ_START");
        private static readonly uint? TraceRamReadEnd = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_READ_END");
        private static readonly int TraceRamReadLimit = ParseOptionalPositiveInt("EUTHERDRIVE_PSX_TRACE_RAM_READ_LIMIT", 4096);
        private static readonly uint? TraceRamWriteStart = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_START");
        private static readonly uint? TraceRamWriteEnd = ParseOptionalHexEnv("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_END");
        private static readonly int TraceRamWriteLimit = ParseOptionalPositiveInt("EUTHERDRIVE_PSX_TRACE_RAM_WRITE_LIMIT", 4096);
        private static readonly bool TraceCdDma = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_TRACE_CD_DMA") == "1";
        private static readonly bool TraceRamReadEnabled = TraceRamReadStart.HasValue && TraceRamReadEnd.HasValue;
        private static readonly bool TraceRamWriteEnabled = TraceRamWriteStart.HasValue && TraceRamWriteEnd.HasValue;
        private static int s_traceRamReadCount;
        private static int s_traceRamWriteCount;
        private const int SpuTickBatchCycles = 96;

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
        private int recentMmioAccessBudget;
        [NonSerialized] private Action<uint, int>? ramWriteObserver;
        [NonSerialized] private Action? memoryCacheControlObserver;

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
            recentMmioAccessBudget = 0;
        }

        public void SetRamWriteObserver(Action<uint, int> observer) {
            ramWriteObserver = observer;
        }

        public void SetMemoryCacheControlObserver(Action observer) {
            memoryCacheControlObserver = observer;
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
            recentMmioAccessBudget = 0;
        }

        private static void ReadExactly(BinaryReader reader, Span<byte> buffer) {
            while (!buffer.IsEmpty) {
                int read = reader.Read(buffer);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of stream while loading PSX BUS state.");

                buffer = buffer[read..];
            }
        }

        public unsafe uint load32(uint address) {
            if (address == MemoryCacheControlAddress) {
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                return memoryCache;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            MarkExecutionSensitiveAccess(addr);
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                uint value = load<uint>(physical, ramPtr);
                if (TraceRamReadEnabled) {
                    TraceRamRead(physical, 4, value);
                }
                return value;
            } else if (addr < 0x1F80_0000) {
                return load<uint>(addr & 0x7_FFFF, ex1Ptr);
            } else if (addr < 0x1f80_0400) {
                return load<uint>(addr & 0x3FF, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                return load<uint>(addr & 0xF, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                return joypad.load(addr);
            } else if (addr < 0x1F80_1060) {
                if (addr == 0x1F80_1054) return Sio1StatusDefault;
                return load<uint>(addr & 0xF, sio);
            } else if (addr < 0x1F80_1070) {
                return load<uint>(addr & 0xF, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                return interruptController.load(addr);
            } else if (addr < 0x1F80_1100) {
                return dma.load(addr);
            } else if (addr < 0x1F80_1140) {
                return timers.load(addr);
            } else if (addr <= 0x1F80_1803) {
                return cdrom.load(addr);
            } else if (addr == 0x1F80_1810) {
                return gpu.loadGPUREAD();
            } else if (addr == 0x1F80_1814) {
                return gpu.loadGPUSTAT();
            } else if (addr == 0x1F80_1820) {
                return mdec.readMDEC0_Data();
            } else if (addr == 0x1F80_1824) {
                return mdec.readMDEC1_Status();
            } else if (addr < 0x1F80_2000) {
                return spu.load(addr);
            } else if (addr < 0x1F80_4000) {
                return exp2.load(addr);
            } else if (addr < 0x1FC8_0000) {
                return load<uint>(addr & 0x7_FFFF, biosPtr);
            } else {
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Load32 Unsupported: {addr:x8} pc={CPU.TraceCurrentPC:x8}");
                return 0xFFFF_FFFF;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryLoadData32Fast(uint address, out uint value) {
            if (address == MemoryCacheControlAddress) {
                value = memoryCache;
                return true;
            }

            uint addr = address & RegionMask[address >> 29];
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                value = load<uint>(physical, ramPtr);
                if (TraceRamReadEnabled) {
                    TraceRamRead(physical, 4, value);
                }
                return true;
            }

            if (addr < 0x1F80_0000) {
                value = load<uint>(addr & 0x7_FFFF, ex1Ptr);
                return true;
            }

            if (addr < 0x1F80_0400) {
                value = load<uint>(addr & 0x3FF, scrathpadPtr);
                return true;
            }

            if (addr >= 0x1FC0_0000 && addr < 0x1FC8_0000) {
                value = load<uint>(addr & 0x7_FFFF, biosPtr);
                return true;
            }

            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanLoadData32Fast(uint address) {
            if (address == MemoryCacheControlAddress) {
                return false;
            }

            uint addr = address & RegionMask[address >> 29];
            return addr < 0x1F80_0400 || (addr >= 0x1FC0_0000 && addr < 0x1FC8_0000);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryLoadData16Fast(uint address, out ushort value) {
            uint addr = address & RegionMask[address >> 29];
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                value = load<ushort>(physical, ramPtr);
                if (TraceRamReadEnabled) {
                    TraceRamRead(physical, 2, value);
                }
                return true;
            }

            if (addr < 0x1F80_0000) {
                value = load<ushort>(addr & 0x7_FFFF, ex1Ptr);
                return true;
            }

            if (addr < 0x1F80_0400) {
                value = load<ushort>(addr & 0x3FF, scrathpadPtr);
                return true;
            }

            if (addr >= 0x1FC0_0000 && addr < 0x1FC8_0000) {
                value = load<ushort>(addr & 0x7_FFFF, biosPtr);
                return true;
            }

            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanLoadData16Fast(uint address) {
            uint addr = address & RegionMask[address >> 29];
            return addr < 0x1F80_0400 || (addr >= 0x1FC0_0000 && addr < 0x1FC8_0000);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryLoadData8Fast(uint address, out byte value) {
            uint addr = address & RegionMask[address >> 29];
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                value = load<byte>(physical, ramPtr);
                if (TraceRamReadEnabled) {
                    TraceRamRead(physical, 1, value);
                }
                return true;
            }

            if (addr < 0x1F80_0000) {
                value = load<byte>(addr & 0x7_FFFF, ex1Ptr);
                return true;
            }

            if (addr < 0x1F80_0400) {
                value = load<byte>(addr & 0x3FF, scrathpadPtr);
                return true;
            }

            if (addr >= 0x1FC0_0000 && addr < 0x1FC8_0000) {
                value = load<byte>(addr & 0x7_FFFF, biosPtr);
                return true;
            }

            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanLoadData8Fast(uint address) {
            uint addr = address & RegionMask[address >> 29];
            return addr < 0x1F80_0400 || (addr >= 0x1FC0_0000 && addr < 0x1FC8_0000);
        }

        public unsafe void write32(uint address, uint value) {
            if (address == MemoryCacheControlAddress) {
                memoryCache = value;
                memoryCacheWriteCount++;
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                memoryCacheControlObserver?.Invoke();
                return;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            MarkExecutionSensitiveAccess(addr);
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                if (TraceRamWriteEnabled) {
                    TraceRamWrite(physical, 4, value, "cpu");
                }
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 4);
            } else if (addr < 0x1F80_0000) {
                write(addr & 0x7_FFFF, value, ex1Ptr);
            } else if (addr < 0x1f80_0400) {
                write(addr & 0x3FF, value, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                write(addr & 0x3F, value, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                joypad.write(addr, value);
            } else if (addr < 0x1F80_1060) {
                write(addr & 0xF, value, sio);
            } else if (addr < 0x1F80_1070) {
                write(addr & 0xF, value, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                interruptController.write(addr, value);
            } else if (addr < 0x1F80_1100) {
                dma.write(addr, value);
            } else if (addr < 0x1F80_1140) {
                timers.write(addr, value);
            } else if (addr < 0x1F80_1810) {
                cdrom.write(addr, value);
            } else if (addr < 0x1F80_1820) {
                gpu.write(addr, value);
            } else if (addr < 0x1F80_1830) {
                mdec.write(addr, value);
            } else if (addr < 0x1F80_2000) {
                spu.write(addr, (ushort)value);
            } else if (addr < 0x1F80_4000) {
                exp2.write(addr, value);
            } else {
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Write32 Unsupported: {addr:x8}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryStoreData32Fast(uint address, uint value) {
            if (address == MemoryCacheControlAddress) {
                memoryCache = value;
                memoryCacheWriteCount++;
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                memoryCacheControlObserver?.Invoke();
                return true;
            }

            uint addr = address & RegionMask[address >> 29];
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                if (TraceRamWriteEnabled) {
                    TraceRamWrite(physical, 4, value, "cpu");
                }
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 4);
                return true;
            }

            if (addr < 0x1F80_0000) {
                write(addr & 0x7_FFFF, value, ex1Ptr);
                return true;
            }

            if (addr < 0x1F80_0400) {
                write(addr & 0x3FF, value, scrathpadPtr);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanStoreData32Fast(uint address) {
            if (address == MemoryCacheControlAddress) {
                return false;
            }

            uint addr = address & RegionMask[address >> 29];
            return addr < 0x1F80_0400;
        }

        public unsafe void write16(uint address, ushort value) {
            if (address == MemoryCacheControlAddress) {
                memoryCache = value;
                memoryCacheWriteCount++;
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                memoryCacheControlObserver?.Invoke();
                return;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            MarkExecutionSensitiveAccess(addr);
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                if (TraceRamWriteEnabled) {
                    TraceRamWrite(physical, 2, value, "cpu");
                }
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 2);
            } else if (addr < 0x1F80_0000) {
                write(addr & 0x7_FFFF, value, ex1Ptr);
            } else if (addr < 0x1F80_0400) {
                write(addr & 0x3FF, value, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                write(addr & 0x3F, value, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                joypad.write(addr, value);
            } else if (addr < 0x1F80_1060) {
                write(addr & 0xF, value, sio);
            } else if (addr < 0x1F80_1070) {
                write(addr & 0xF, value, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                interruptController.write(addr, value);
            } else if (addr < 0x1F80_1100) {
                dma.write(addr, value);
            } else if (addr < 0x1F80_1140) {
                timers.write(addr, value);
            } else if (addr < 0x1F80_1810) {
                cdrom.write(addr, value);
            } else if (addr < 0x1F80_1820) {
                gpu.write(addr, value);
            } else if (addr < 0x1F80_1830) {
                mdec.write(addr, value);
            } else if (addr < 0x1F80_2000) {
                spu.write(addr, value);
            } else if (addr < 0x1F80_4000) {
                exp2.write(addr, value);
            } else {
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Write16 Unsupported: {addr:x8}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryStoreData16Fast(uint address, ushort value) {
            if (address == MemoryCacheControlAddress) {
                memoryCache = value;
                memoryCacheWriteCount++;
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                memoryCacheControlObserver?.Invoke();
                return true;
            }

            uint addr = address & RegionMask[address >> 29];
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                if (TraceRamWriteEnabled) {
                    TraceRamWrite(physical, 2, value, "cpu");
                }
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 2);
                return true;
            }

            if (addr < 0x1F80_0000) {
                write(addr & 0x7_FFFF, value, ex1Ptr);
                return true;
            }

            if (addr < 0x1F80_0400) {
                write(addr & 0x3FF, value, scrathpadPtr);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanStoreData16Fast(uint address) {
            if (address == MemoryCacheControlAddress) {
                return false;
            }

            uint addr = address & RegionMask[address >> 29];
            return addr < 0x1F80_0400;
        }

        public unsafe void write8(uint address, byte value) {
            if (address == MemoryCacheControlAddress) {
                memoryCache = value;
                memoryCacheWriteCount++;
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                memoryCacheControlObserver?.Invoke();
                return;
            }

            uint i = address >> 29;
            uint addr = address & RegionMask[i];
            MarkExecutionSensitiveAccess(addr);
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                if (TraceRamWriteEnabled) {
                    TraceRamWrite(physical, 1, value, "cpu");
                }
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 1);
            } else if (addr < 0x1F80_0000) {
                write(addr & 0x7_FFFF, value, ex1Ptr);
            } else if (addr < 0x1f80_0400) {
                write(addr & 0x3FF, value, scrathpadPtr);
            } else if (addr < 0x1F80_1040) {
                write(addr & 0x3F, value, memoryControl1);
            } else if (addr < 0x1F80_1050) {
                joypad.write(addr, value);
            } else if (addr < 0x1F80_1060) {
                write(addr & 0xF, value, sio);
            } else if (addr < 0x1F80_1070) {
                write(addr & 0xF, value, memoryControl2);
            } else if (addr < 0x1F80_1080) {
                interruptController.write(addr, value);
            } else if (addr < 0x1F80_1100) {
                dma.write(addr, value);
            } else if (addr < 0x1F80_1140) {
                timers.write(addr, value);
            } else if (addr < 0x1F80_1810) {
                cdrom.write(addr, value);
            } else if (addr < 0x1F80_1820) {
                gpu.write(addr, value);
            } else if (addr < 0x1F80_1830) {
                mdec.write(addr, value);
            } else if (addr < 0x1F80_2000) {
                spu.write(addr, value);
            } else if (addr < 0x1F80_4000) {
                exp2.write(addr, value);
            } else {
                if (VerboseBusAccess)
                    Console.WriteLine($"[BUS] Write8 Unsupported: {addr:x8}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryStoreData8Fast(uint address, byte value) {
            if (address == MemoryCacheControlAddress) {
                memoryCache = value;
                memoryCacheWriteCount++;
                MarkExecutionSensitiveAccess(MemoryCacheControlAddress);
                memoryCacheControlObserver?.Invoke();
                return true;
            }

            uint addr = address & RegionMask[address >> 29];
            if (addr < 0x1F00_0000) {
                uint physical = addr & 0x1F_FFFF;
                if (TraceRamWriteEnabled) {
                    TraceRamWrite(physical, 1, value, "cpu");
                }
                write(physical, value, ramPtr);
                ramWriteObserver?.Invoke(physical, 1);
                return true;
            }

            if (addr < 0x1F80_0000) {
                write(addr & 0x7_FFFF, value, ex1Ptr);
                return true;
            }

            if (addr < 0x1F80_0400) {
                write(addr & 0x3FF, value, scrathpadPtr);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanStoreData8Fast(uint address) {
            if (address == MemoryCacheControlAddress) {
                return false;
            }

            uint addr = address & RegionMask[address >> 29];
            return addr < 0x1F80_0400;
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
            if (recentMmioAccessBudget > 0) {
                recentMmioAccessBudget = Math.Max(0, recentMmioAccessBudget - cycles);
            }
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe T load<T>(uint addr, byte* ptr) where T : unmanaged {
            return Unsafe.ReadUnaligned<T>(ptr + addr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void write<T>(uint addr, T value, byte* ptr) where T : unmanaged {
            Unsafe.WriteUnaligned(ptr + addr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint LoadFromRam(uint addr) {
            return *(uint*)(ramPtr + (addr & 0x1F_FFFF));
        }

        public DMA DMAController => dma;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe uint LoadFromBios(uint addr) {
            return *(uint*)(biosPtr + (addr & 0x7_FFFF));
        }

        public uint MemoryCacheControl => memoryCache;

        public uint MemoryCacheWriteCount => memoryCacheWriteCount;
        public DMA Dma => dma;
        public bool ShouldUseTightTickBatch =>
            recentMmioAccessBudget > 0
            || interruptController.interruptPending()
            || dma.HasPendingWork
            || cdrom.HasPendingWork
            || joypad.HasPendingWork;
        public bool ShouldYieldCpuSlice =>
            recentMmioAccessBudget > 0
            || interruptController.interruptPending();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span<uint> DmaFromRam(uint addr, uint size) {
            uint physical = addr & 0x1F_FFFF;
            if (TraceRamReadEnabled) {
                TraceRamDmaRead(physical, size);
            }
            return new Span<uint>(ramPtr + physical, (int)size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaToRam(uint addr, uint value) {
            uint physical = addr & 0x1F_FFFF;
            if (TraceRamWriteEnabled) {
                TraceRamWrite(physical, 4, value, "dma");
            }
            *(uint*)(ramPtr + physical) = value;
            ramWriteObserver?.Invoke(physical, 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaToRam(uint addr, byte[] buffer, uint size) {
            uint physical = addr & 0x1F_FFFF;
            int byteCount = (int)size * 4;
            if (TraceRamWriteEnabled && ShouldTraceRamWriteRange(physical, (uint)byteCount)) {
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
        private void MarkExecutionSensitiveAccess(uint address) {
            if (address == MemoryCacheControlAddress
                || (address >= MmioStartAddress && address < MmioEndAddressExclusive)) {
                recentMmioAccessBudget = RecentMmioAccessHysteresisCycles;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void DmaFromCD(uint address, int size) {
            var dma = cdrom.processDmaLoad(size);
            uint physical = address & 0x1F_FFFC;
            if (TraceCdDma || (TraceRamWriteEnabled && ShouldTraceRamWriteRange(physical, (uint)(dma.Length * 4)))) {
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
