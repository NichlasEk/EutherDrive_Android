using System;
using System.Diagnostics;
using System.Text;
using ProjectPSX.Devices;
using ProjectPSX.Devices.CdRom;
using ProjectPSX.Devices.Expansion;
using ProjectPSX.Devices.Input;

namespace ProjectPSX {
    public class ProjectPSX {
        const int PSX_MHZ = 33868800;
        const int MIPS_UNDERCLOCK = 3; //Testing: This compensates the ausence of HALT instruction on MIPS Architecture, may broke some games.
        const double FPS_PAL = PSX_MHZ / ((3406.0 * 314.0 * 7.0) / 11.0);
        const double FPS_NTSC = PSX_MHZ / ((3413.0 * 263.0 * 7.0) / 11.0);
        private static readonly int TightBusTickBatchCycles = ParseBusTickBatchCycles();
        private static readonly int RelaxedBusTickBatchCycles = ParseBusTickRelaxedBatchCycles();
        private static readonly uint[] DebugPeekAddresses = ParseOptionalHexAddrEnv("EUTHERDRIVE_PSX_TRACE_PEEK_ADDRS");
        private static readonly bool DebugPointerPeeks = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_TRACE_POINTER_PEEKS") == "1";

        private CPU cpu;
        private BUS bus;
        private CDROM cdrom;
        private GPU gpu;
        private SPU spu;
        private JOYPAD joypad;
        private TIMERS timers;
        private MDEC mdec;
        private DigitalController controller;
        private MemoryCard memoryCard;
        private CD cd;
        private InterruptController interruptController;
        private Exp2 exp2;
        private bool _psxBootBiosExited;
        private double? _frameRateOverrideHz;
        private long _perfWindowStartTicks = Stopwatch.GetTimestamp();
        private int _perfWindowFrames;
        private long _perfCpuInstructions;
        private long _perfCpuBranches;
        private long _perfCpuLoad8;
        private long _perfCpuLoad16;
        private long _perfCpuLoad32;
        private long _perfCpuStore8;
        private long _perfCpuStore16;
        private long _perfCpuStore32;
        private long _perfICacheHits;
        private long _perfICacheMisses;
        private long _perfBusTickCalls;
        private long _perfBusTickCycles;
        private long _perfBusFastReads;
        private long _perfBusMmioReads;
        private long _perfBusFastWrites;
        private long _perfBusMmioWrites;
        private long _perfIrqChecks;
        private long _perfInterruptHandles;
        private long _perfGpuTriangles;
        private long _perfGpuRectangles;
        private long _perfGpuLines;
        private long _perfGpuCopies;
        private long _perfSpuMixedSamples;
        private long _perfSpuActiveVoices;
        private volatile string _perfSummary = "PSX core --";

        public ProjectPSX(
            IHostWindow window,
            string diskFilename,
            bool analogControllerEnabled = true,
            bool fastLoadEnabled = false,
            bool superFastLoadEnabled = false,
            string? subchannelOverridePath = null) {
            controller = new DigitalController(analogControllerEnabled);
            memoryCard = new MemoryCard();

            interruptController = new InterruptController();

            cd = new CD(diskFilename, subchannelOverridePath);
            spu = new SPU(window, interruptController);
            gpu = new GPU(window);
            cdrom = new CDROM(cd, spu);
            cdrom.SetFastLoadEnabled(fastLoadEnabled);
            cdrom.SetSuperFastLoadEnabled(superFastLoadEnabled);
            joypad = new JOYPAD(controller, memoryCard);
            timers = new TIMERS();
            mdec = new MDEC();
            exp2 = new Exp2();
            bus = new BUS(gpu, cdrom, spu, joypad, timers, mdec, interruptController, exp2);
            cpu = new CPU(bus);
            bus.SetRamWriteObserver(cpu.ObserveRamWrite);

            if (!bus.loadBios()) {
                throw new InvalidOperationException("PSX BIOS not found or failed to load.");
            }
            if (diskFilename.EndsWith(".exe")) {
                bus.loadEXE(diskFilename);
            } else if (superFastLoadEnabled) {
                PsxDiscBootResolver.ResolvedExecutable resolvedExecutable = PsxDiscBootResolver.TryResolveExecutable(cd);
                if (resolvedExecutable != null) {
                    Console.WriteLine($"[PSX-SUPERFAST] Resolved {resolvedExecutable.BootPath} entry=0x{resolvedExecutable.EntryPoint:x8}");
                    bus.loadEXE(resolvedExecutable.ExecutableBytes, $"disc:{resolvedExecutable.BootPath}");
                }
            }
        }

        public void RunFrame() {
            cpu.ResetPerfCounters();
            bus.ResetPerfCounters();
            gpu.ResetPerfCounters();
            spu.ResetPerfCounters();
            int cpuCyclesThisFrame = 0;
            int pendingBusCycles = 0;
            int cpuCyclesPerFrame = GetCpuCyclesPerFrame();
            while (cpuCyclesThisFrame < cpuCyclesPerFrame) {
                int cpuCycles = cpu.Run();
                cpuCyclesThisFrame += cpuCycles;
                pendingBusCycles += cpuCycles * MIPS_UNDERCLOCK;
                uint physicalPc = cpu.CurrentPC & 0x1FFF_FFFF;
                if (!_psxBootBiosExited && physicalPc < 0x1FC0_0000 && physicalPc >= 0x0001_0000) {
                    _psxBootBiosExited = true;
                    cdrom.NotifyBiosExited();
                    cpu.NotifyBiosExited();
                }

                bool busFlushed = false;
                int busTickBatchCycles = (!_psxBootBiosExited || bus.RequiresFrequentSync)
                    ? TightBusTickBatchCycles
                    : RelaxedBusTickBatchCycles;

                if (pendingBusCycles >= busTickBatchCycles) {
                    bus.tick(pendingBusCycles);
                    pendingBusCycles = 0;
                    busFlushed = true;
                }

                bool interruptPending = false;
                if (!busFlushed && bus.RequiresFrequentSync) {
                    _perfIrqChecks++;
                    interruptPending = bus.interruptController.interruptPending();
                }

                if (busFlushed || interruptPending) {
                    _perfInterruptHandles++;
                    cpu.handleInterrupts();
                }
            }

            if (pendingBusCycles > 0) {
                bus.tick(pendingBusCycles);
                _perfInterruptHandles++;
                cpu.handleInterrupts();
            }

            UpdatePerfSummary(
                cpu.CapturePerfSnapshot(),
                bus.CapturePerfSnapshot(),
                gpu.CapturePerfSnapshot(),
                spu.CapturePerfSnapshot());
        }

        public void JoyPadUp(GamepadInputsEnum button) => controller.handleJoyPadUp(button);

        public void JoyPadDown(GamepadInputsEnum button) => controller.handleJoyPadDown(button);

        public void SetAnalogControllerEnabled(bool enabled) => controller.SetAnalogControllerEnabled(enabled);

        public void SetFastLoadEnabled(bool enabled) => cdrom.SetFastLoadEnabled(enabled);

        public void SetFrameRateOverrideHz(double? hz) {
            if (hz.HasValue && hz.Value > 0) {
                _frameRateOverrideHz = hz.Value;
            } else {
                _frameRateOverrideHz = null;
            }
        }

        public void SetVideoStandardOverride(bool? forcePal) => gpu.SetVideoStandardOverride(forcePal);

        public double GetTargetFps() {
            if (_frameRateOverrideHz.HasValue)
                return _frameRateOverrideHz.Value;

            return gpu.IsPalMode ? FPS_PAL : FPS_NTSC;
        }

        public void toggleDebug() {
            cpu.debug = !cpu.debug;
            gpu.debug = !gpu.debug;
        }

        public void toggleCdRomLid() {
            cdrom.toggleLid();
        }

        public uint DebugCurrentPC => cpu.CurrentPC;
        public object CpuStateObject => cpu;
        public BUS Bus => bus;
        public GPU GPU => gpu;
        public SPU SPU => spu;
        public CDROM CDROM => cdrom;
        public JOYPAD JOYPAD => joypad;
        public TIMERS Timers => timers;
        public MDEC MDEC => mdec;
        public InterruptController InterruptController => interruptController;
        public bool BootBiosExited
        {
            get => _psxBootBiosExited;
            set => _psxBootBiosExited = value;
        }

        public string DebugStartSummary() {
            uint d370 = bus.LoadFromRam(0x0003_D370);
            uint d374 = bus.LoadFromRam(0x0003_D374);
            uint d378 = bus.LoadFromRam(0x0003_D378);
            uint at = cpu.GetRegister(1);
            uint v0 = cpu.GetRegister(2);
            uint v1 = cpu.GetRegister(3);
            uint a0 = cpu.GetRegister(4);
            uint a1 = cpu.GetRegister(5);
            uint a2 = cpu.GetRegister(6);
            uint a3 = cpu.GetRegister(7);
            uint cop0r3 = cpu.GetCop0Register(3);
            uint cop0r9 = cpu.GetCop0Register(9);
            uint sp = cpu.StackPointer;
            uint ra = cpu.ReturnAddress;
            var text = new StringBuilder();
            text.Append($"pc={cpu.CurrentPC:x8} biosExited={(_psxBootBiosExited ? 1 : 0)} ");
            text.Append($"at={at:x8} v0={v0:x8} v1={v1:x8} ");
            text.Append($"a0={a0:x8} a1={a1:x8} a2={a2:x8} a3={a3:x8} ");
            text.Append($"c03={cop0r3:x8} c09={cop0r9:x8} ");
            text.Append($"sp={sp:x8} ra={ra:x8} ");
            text.Append($"d370={d370:x8} d374={d374:x8} d378={d378:x8} ");

            if (DebugPointerPeeks) {
                AppendPointerPeek(text, "at", at);
                AppendPointerPeek(text, "a0", a0);
                AppendPointerPeek(text, "a1", a1);
                AppendPointerPeek(text, "a2", a2);
                AppendPointerPeek(text, "sp", sp);
            }

            AppendConfiguredPeeks(text);

            text.Append($"irq=({interruptController.DebugISTAT:x3}/{interruptController.DebugIMASK:x3}) ");
            text.Append($"{bus.DMAController.DebugSummary(2)} ");
            text.Append($"{bus.DMAController.DebugSummary(3)} ");
            text.Append($"{bus.DMAController.DebugSummary(4)} ");
            text.Append($"{bus.DMAController.DebugSummary(7)} ");
            text.Append($"{gpu.DebugSummary()} {mdec.DebugSummary()} {cdrom.DebugSummary()}");
            return text.ToString();
        }

        public string DebugCodeWindow(int wordsBefore = 8, int wordsAfter = 16) {
            return DebugCodeWindowAt(cpu.CurrentPC, wordsBefore, wordsAfter);
        }

        public string DebugCodeWindowAt(uint address, int wordsBefore = 8, int wordsAfter = 16) {
            uint pc = address;
            uint start = pc - (uint)(wordsBefore * 4);
            var text = new System.Text.StringBuilder();
            text.AppendLine($"pc={pc:x8}");
            for (int i = -wordsBefore; i <= wordsAfter; i++) {
                uint addr = pc + (uint)(i * 4);
                uint instr = bus.load32(addr);
                text.Append(i == 0 ? "=>" : "  ");
                text.Append($" {addr:x8}: {instr:x8}");
                text.AppendLine();
            }
            return text.ToString();
        }

        public bool TryGetPerfSummary(out string summary) {
            summary = _perfSummary;
            return !string.IsNullOrWhiteSpace(summary);
        }

        private static int ParseBusTickBatchCycles() {
            // Spyro YOTD and similar titles busy-wait on GPU/timer-visible state during CD-driven loads.
            // A coarse device flush batch leaves those MMIO reads stale long enough to stall loading.
            // Keep the default close to instruction cadence until the underlying timing model is improved.
            const int fallback = 6;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BUS_TICK_BATCH_CYCLES");
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            return int.TryParse(raw, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private static int ParseBusTickRelaxedBatchCycles() {
            const int fallback = 24;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BUS_TICK_RELAXED_BATCH_CYCLES");
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            return int.TryParse(raw, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private int GetCpuCyclesPerFrame() {
            return (int)Math.Round((PSX_MHZ / GetTargetFps()) / MIPS_UNDERCLOCK);
        }

        private void UpdatePerfSummary(CPU.PerfSnapshot cpuPerf, BUS.PerfSnapshot busPerf, GPU.PerfSnapshot gpuPerf, SPU.PerfSnapshot spuPerf) {
            _perfWindowFrames++;
            _perfCpuInstructions += cpuPerf.Instructions;
            _perfCpuBranches += cpuPerf.BranchInstructions;
            _perfCpuLoad8 += cpuPerf.Load8Ops;
            _perfCpuLoad16 += cpuPerf.Load16Ops;
            _perfCpuLoad32 += cpuPerf.Load32Ops;
            _perfCpuStore8 += cpuPerf.Store8Ops;
            _perfCpuStore16 += cpuPerf.Store16Ops;
            _perfCpuStore32 += cpuPerf.Store32Ops;
            _perfICacheHits += cpuPerf.ICacheHits;
            _perfICacheMisses += cpuPerf.ICacheMisses;
            _perfBusTickCalls += busPerf.TickCalls;
            _perfBusTickCycles += busPerf.TickCycles;
            _perfBusFastReads += busPerf.Load32Ram + busPerf.Load32Ex1 + busPerf.Load32Scratchpad + busPerf.Load32Bios;
            _perfBusMmioReads += busPerf.Load32Mmio;
            _perfBusFastWrites +=
                busPerf.Write32Ram + busPerf.Write16Ram + busPerf.Write8Ram +
                busPerf.Write32Ex1 + busPerf.Write16Ex1 + busPerf.Write8Ex1 +
                busPerf.Write32Scratchpad + busPerf.Write16Scratchpad + busPerf.Write8Scratchpad;
            _perfBusMmioWrites += busPerf.Write32Mmio + busPerf.Write16Mmio + busPerf.Write8Mmio;
            _perfGpuTriangles += gpuPerf.TrianglePrimitives;
            _perfGpuRectangles += gpuPerf.RectanglePrimitives;
            _perfGpuLines += gpuPerf.LineSegments;
            _perfGpuCopies += gpuPerf.VramToVramCopies + gpuPerf.CpuToVramCopies + gpuPerf.VramToCpuCopies;
            _perfSpuMixedSamples += spuPerf.MixedSamples;
            _perfSpuActiveVoices += spuPerf.ActiveVoicesAccumulated;

            long nowTicks = Stopwatch.GetTimestamp();
            if ((nowTicks - _perfWindowStartTicks) < Stopwatch.Frequency / 4 || _perfWindowFrames <= 0) {
                return;
            }

            double frames = _perfWindowFrames;
            double iCacheTotal = _perfICacheHits + _perfICacheMisses;
            double iCacheHitRate = iCacheTotal > 0 ? (_perfICacheHits * 100.0) / iCacheTotal : 0;
            double avgActiveVoices = _perfSpuMixedSamples > 0 ? _perfSpuActiveVoices / (double)_perfSpuMixedSamples : 0;

            _perfSummary =
                $"PSX core  cpu instr:{_perfCpuInstructions / frames:0} br:{_perfCpuBranches / frames:0} ld:{_perfCpuLoad8 / frames:0}/{_perfCpuLoad16 / frames:0}/{_perfCpuLoad32 / frames:0} st:{_perfCpuStore8 / frames:0}/{_perfCpuStore16 / frames:0}/{_perfCpuStore32 / frames:0} ic:{iCacheHitRate:0}%\n" +
                $"PSX mix  bus tick:{_perfBusTickCalls / frames:0.0} cyc:{_perfBusTickCycles / frames:0} fastR:{_perfBusFastReads / frames:0} mmioR:{_perfBusMmioReads / frames:0} fastW:{_perfBusFastWrites / frames:0} mmioW:{_perfBusMmioWrites / frames:0} irq:{_perfIrqChecks / frames:0.0}/{_perfInterruptHandles / frames:0.0} gpu tri:{_perfGpuTriangles / frames:0.0} rect:{_perfGpuRectangles / frames:0.0} line:{_perfGpuLines / frames:0.0} copy:{_perfGpuCopies / frames:0.0} spu samp:{_perfSpuMixedSamples / frames:0} actV:{avgActiveVoices:0.0}";

            _perfWindowStartTicks = nowTicks;
            _perfWindowFrames = 0;
            _perfCpuInstructions = 0;
            _perfCpuBranches = 0;
            _perfCpuLoad8 = 0;
            _perfCpuLoad16 = 0;
            _perfCpuLoad32 = 0;
            _perfCpuStore8 = 0;
            _perfCpuStore16 = 0;
            _perfCpuStore32 = 0;
            _perfICacheHits = 0;
            _perfICacheMisses = 0;
            _perfBusTickCalls = 0;
            _perfBusTickCycles = 0;
            _perfBusFastReads = 0;
            _perfBusMmioReads = 0;
            _perfBusFastWrites = 0;
            _perfBusMmioWrites = 0;
            _perfIrqChecks = 0;
            _perfInterruptHandles = 0;
            _perfGpuTriangles = 0;
            _perfGpuRectangles = 0;
            _perfGpuLines = 0;
            _perfGpuCopies = 0;
            _perfSpuMixedSamples = 0;
            _perfSpuActiveVoices = 0;
        }

        private void AppendConfiguredPeeks(StringBuilder text) {
            if (DebugPeekAddresses.Length == 0) {
                return;
            }

            text.Append("peek[");
            for (int i = 0; i < DebugPeekAddresses.Length; i++) {
                uint addr = DebugPeekAddresses[i];
                if (i != 0) {
                    text.Append(' ');
                }

                if (TryLoadRamWord(addr, out uint value)) {
                    text.Append($"{addr:x8}:{value:x8}");
                } else {
                    text.Append($"{addr:x8}:--------");
                }
            }
            text.Append("] ");
        }

        private void AppendPointerPeek(StringBuilder text, string label, uint pointer) {
            if (!TryLoadRamWord(pointer, out uint at0)) {
                return;
            }

            text.Append($"{label}[");
            if (TryLoadRamWord(pointer - 8, out uint minus8)) {
                text.Append($"-8:{minus8:x8} ");
            }
            if (TryLoadRamWord(pointer - 4, out uint minus4)) {
                text.Append($"-4:{minus4:x8} ");
            }
            text.Append($"0:{at0:x8}");
            if (TryLoadRamWord(pointer + 4, out uint plus4)) {
                text.Append($" +4:{plus4:x8}");
            }
            text.Append("] ");
        }

        private bool TryLoadRamWord(uint address, out uint value) {
            uint physical = address & 0x1FFF_FFFF;
            bool isMappedRam = physical < 0x0020_0000;
            bool looksLikeRamPointer = address < 0x0020_0000 || (address >= 0x8000_0000 && address < 0x8020_0000);
            if (!isMappedRam || !looksLikeRamPointer) {
                value = 0;
                return false;
            }

            value = bus.LoadFromRam(physical);
            return true;
        }

        private static uint[] ParseOptionalHexAddrEnv(string name) {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) {
                return Array.Empty<uint>();
            }

            string[] parts = raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            uint[] values = new uint[parts.Length];
            int count = 0;
            foreach (string part in parts) {
                string token = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
                if (uint.TryParse(token, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value)) {
                    values[count++] = value;
                }
            }

            if (count == values.Length) {
                return values;
            }

            uint[] trimmed = new uint[count];
            Array.Copy(values, trimmed, count);
            return trimmed;
        }

    }
}
