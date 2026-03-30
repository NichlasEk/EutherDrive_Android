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
        private enum SyncGovernorMode {
            Off,
            Auto,
            Aggressive
        }

        private static readonly int TightBusTickBatchCycles = ParseBusTickBatchCycles();
        private static readonly int RelaxedBusTickBatchCycles = ParseBusTickRelaxedBatchCycles();
        private static readonly SyncGovernorMode GameplaySyncGovernorMode = ParseSyncGovernorMode();
        private static readonly int SyncGovernorMaxBatchCycles = ParseSyncGovernorMaxBatchCycles();
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
        private long _perfInterruptNoPendingReturns;
        private long _perfInterruptMaskedReturns;
        private long _perfInterruptGteDeferrals;
        private long _perfInterruptExceptionsTaken;
        private long _perfGpuTriangles;
        private long _perfGpuRectangles;
        private long _perfGpuLines;
        private long _perfGpuCopies;
        private long _perfSpuMixedSamples;
        private long _perfSpuActiveVoices;
        private volatile string _perfSummary = "PSX core --";
        private int _syncGovernorLevel;
        private int _syncGovernorStableFrames;
        private int _syncGovernorCooldownFrames;
        private int _syncGovernorRelaxedBatchCycles = RelaxedBusTickBatchCycles;
        private int _syncGovernorLastAppliedBatchCycles = RelaxedBusTickBatchCycles;
        private volatile string _syncGovernorSummary = BuildSyncGovernorDisabledSummary();

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
            bus.SetMemoryCacheWriteObserver(cpu.ObserveMemoryCacheControlWrite);

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
            int relaxedBusTickBatchCycles = GetRelaxedBusTickBatchCycles();
            _syncGovernorLastAppliedBatchCycles = relaxedBusTickBatchCycles;
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
                bool requiresFrequentSync = bus.RequiresFrequentSync(relaxedBusTickBatchCycles);
                int busTickBatchCycles = (!_psxBootBiosExited || requiresFrequentSync)
                    ? TightBusTickBatchCycles
                    : relaxedBusTickBatchCycles;

                if (pendingBusCycles >= busTickBatchCycles) {
                    bus.tick(pendingBusCycles);
                    pendingBusCycles = 0;
                    busFlushed = true;
                }

                bool interruptPending = false;
                if (!busFlushed && requiresFrequentSync) {
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

            CPU.PerfSnapshot cpuPerf = cpu.CapturePerfSnapshot();
            BUS.PerfSnapshot busPerf = bus.CapturePerfSnapshot();
            GPU.PerfSnapshot gpuPerf = gpu.CapturePerfSnapshot();
            SPU.PerfSnapshot spuPerf = spu.CapturePerfSnapshot();
            UpdateSyncGovernor(cpuPerf, busPerf, gpuPerf);
            UpdatePerfSummary(
                cpuPerf,
                busPerf,
                gpuPerf,
                spuPerf);
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

        public void ResyncRuntimeStateAfterLoad() => cpu.ResyncAfterLoad();

        private static int ParseBusTickBatchCycles() {
            // Spyro YOTD and similar titles busy-wait on GPU/timer-visible state during CD-driven loads.
            // A coarse device flush batch leaves those MMIO reads stale long enough to stall loading.
            // Keep the default close to instruction cadence until the underlying timing model is improved.
            const int fallback = 24;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BUS_TICK_BATCH_CYCLES");
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            return int.TryParse(raw, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private static int ParseBusTickRelaxedBatchCycles() {
            const int fallback = 96;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BUS_TICK_RELAXED_BATCH_CYCLES");
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            return int.TryParse(raw, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private static SyncGovernorMode ParseSyncGovernorMode() {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_SYNC_GOVERNOR");
            if (string.IsNullOrWhiteSpace(raw)) {
                return SyncGovernorMode.Auto;
            }

            if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) {
                return SyncGovernorMode.Off;
            }

            if (string.Equals(raw, "2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "aggressive", StringComparison.OrdinalIgnoreCase)) {
                return SyncGovernorMode.Aggressive;
            }

            return SyncGovernorMode.Auto;
        }

        private static int ParseSyncGovernorMaxBatchCycles() {
            int fallback = RelaxedBusTickBatchCycles * 8;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_SYNC_GOVERNOR_MAX_BATCH_CYCLES");
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            return int.TryParse(raw, out int parsed) && parsed >= RelaxedBusTickBatchCycles
                ? parsed
                : fallback;
        }

        private int GetCpuCyclesPerFrame() {
            return (int)Math.Round((PSX_MHZ / GetTargetFps()) / MIPS_UNDERCLOCK);
        }

        private int GetRelaxedBusTickBatchCycles() {
            if (GameplaySyncGovernorMode == SyncGovernorMode.Off) {
                return RelaxedBusTickBatchCycles;
            }

            return Math.Max(RelaxedBusTickBatchCycles, _syncGovernorRelaxedBatchCycles);
        }

        private void UpdateSyncGovernor(CPU.PerfSnapshot cpuPerf, BUS.PerfSnapshot busPerf, GPU.PerfSnapshot gpuPerf) {
            if (GameplaySyncGovernorMode == SyncGovernorMode.Off) {
                _syncGovernorLevel = 0;
                _syncGovernorStableFrames = 0;
                _syncGovernorCooldownFrames = 0;
                _syncGovernorRelaxedBatchCycles = RelaxedBusTickBatchCycles;
                _syncGovernorSummary = BuildSyncGovernorDisabledSummary();
                return;
            }

            int readMmioOps = busPerf.ReadOpsMmio;
            int writeMmioOps = busPerf.WriteOpsMmio;
            int load32MmioOps = busPerf.Load32Mmio;
            int gpuScore = (gpuPerf.TrianglePrimitives * 3)
                + (gpuPerf.TexturedTriangles * 2)
                + gpuPerf.SemiTransparentTriangles
                + gpuPerf.RectanglePrimitives
                + gpuPerf.TexturedRectangles
                + gpuPerf.SemiTransparentRectangles
                + (gpuPerf.LineSegments / 2)
                + (gpuPerf.FillRectCommands * 2);
            bool has3dWorkload = gpuPerf.TrianglePrimitives >= 96
                || gpuPerf.TexturedTriangles >= 64
                || gpuScore >= 640;
            bool geometryDominant =
                ((gpuPerf.TrianglePrimitives * 2) + gpuPerf.TexturedTriangles)
                >= (gpuPerf.RectanglePrimitives
                    + (gpuPerf.TexturedRectangles * 2)
                    + (gpuPerf.FillRectCommands * 4)
                    + 48);
            bool pollLight = readMmioOps <= 12 && load32MmioOps <= 4;
            bool pollHeavy = readMmioOps >= 24 || load32MmioOps >= 8;
            bool copyHeavy = gpuPerf.CpuToVramCopies > 0 || gpuPerf.VramToCpuCopies > 0;
            bool irqHeavy = cpuPerf.InterruptExceptionsTaken > 4 || cpuPerf.InterruptMaskedReturns > 24;
            bool uiHeavy = gpuPerf.FillRectCommands >= 8 || gpuPerf.RectanglePrimitives >= 192;
            bool writeDominant = writeMmioOps >= 48 && readMmioOps <= 8;
            bool writeActive = writeMmioOps >= 32 || gpuScore >= 768;
            // Tekken-like 3D scenes tend to be GPU-write heavy with low MMIO polling.
            // Loaders and timing-sensitive loops show up as read-heavy polling, copies or IRQ spikes instead.
            bool gameplayCandidate = _psxBootBiosExited
                && has3dWorkload
                && geometryDominant
                && pollLight
                && !copyHeavy
                && !irqHeavy
                && !uiHeavy
                && writeActive;

            int maxLevel = GameplaySyncGovernorMode == SyncGovernorMode.Aggressive ? 3 : 2;
            if (!_psxBootBiosExited || pollHeavy || copyHeavy || irqHeavy) {
                _syncGovernorLevel = 0;
                _syncGovernorStableFrames = 0;
                _syncGovernorCooldownFrames = 8;
            } else if (gameplayCandidate) {
                if (_syncGovernorCooldownFrames > 0) {
                    _syncGovernorCooldownFrames--;
                } else {
                    _syncGovernorStableFrames += writeDominant ? 2 : 1;
                    int promoteThreshold = GameplaySyncGovernorMode == SyncGovernorMode.Aggressive ? 5 : 8;
                    if (_syncGovernorStableFrames >= promoteThreshold && _syncGovernorLevel < maxLevel) {
                        _syncGovernorLevel++;
                        _syncGovernorStableFrames = 0;
                    }
                }
            } else {
                _syncGovernorStableFrames = 0;
                if (_syncGovernorCooldownFrames > 0) {
                    _syncGovernorCooldownFrames--;
                } else if (_syncGovernorLevel > 0 && (readMmioOps > 12 || !geometryDominant || uiHeavy)) {
                    _syncGovernorLevel--;
                }
            }

            int relaxedBatchCycles = RelaxedBusTickBatchCycles << _syncGovernorLevel;
            if (GameplaySyncGovernorMode == SyncGovernorMode.Aggressive) {
                relaxedBatchCycles = Math.Min(relaxedBatchCycles << 1, SyncGovernorMaxBatchCycles);
            }

            _syncGovernorRelaxedBatchCycles = Math.Clamp(
                relaxedBatchCycles,
                RelaxedBusTickBatchCycles,
                SyncGovernorMaxBatchCycles);
            _syncGovernorSummary =
                $"PSX sync mode:{GameplaySyncGovernorMode.ToString().ToLowerInvariant()} batch:{_syncGovernorLastAppliedBatchCycles}->{_syncGovernorRelaxedBatchCycles} " +
                $"lvl:{_syncGovernorLevel} stable:{_syncGovernorStableFrames} cool:{_syncGovernorCooldownFrames} " +
                $"poll:r{readMmioOps}/l32:{load32MmioOps} w:{writeMmioOps} gpu:{gpuPerf.TrianglePrimitives}/{gpuPerf.TexturedTriangles}/{gpuPerf.RectanglePrimitives} " +
                $"score:{gpuScore} cand:{(gameplayCandidate ? 1 : 0)}";
        }

        private static string BuildSyncGovernorDisabledSummary() {
            return $"PSX sync mode:off batch:{RelaxedBusTickBatchCycles}";
        }

        private static string BuildMmioReadSummary(BUS.PerfSnapshot busPerf) {
            if (busPerf.TopMmioReadCount0 <= 0
                && busPerf.TopMmioReadCount1 <= 0
                && busPerf.TopMmioReadCount2 <= 0
                && busPerf.RelaxedGpuStatReads <= 0
                && busPerf.RelaxedJoyStatusReads <= 0
                && busPerf.RelaxedTimer2Reads <= 0
                && busPerf.MmioShadowHits <= 0) {
                return string.Empty;
            }

            var text = new StringBuilder("PSX poll ");
            AppendMmioReadHotspot(text, busPerf.TopMmioReadAddr0, busPerf.TopMmioReadCount0);
            AppendMmioReadHotspot(text, busPerf.TopMmioReadAddr1, busPerf.TopMmioReadCount1);
            AppendMmioReadHotspot(text, busPerf.TopMmioReadAddr2, busPerf.TopMmioReadCount2);
            if (busPerf.RelaxedGpuStatReads > 0
                || busPerf.RelaxedJoyStatusReads > 0
                || busPerf.RelaxedTimer2Reads > 0
                || busPerf.MmioShadowHits > 0) {
                if (text.Length > "PSX poll ".Length) {
                    text.Append(' ');
                }

                if (busPerf.RelaxedGpuStatReads > 0) {
                    text.Append($"gstRelax:{busPerf.RelaxedGpuStatReads}");
                }

                if (busPerf.RelaxedJoyStatusReads > 0) {
                    if (text[^1] != ' ') {
                        text.Append(' ');
                    }

                    text.Append($"jstRelax:{busPerf.RelaxedJoyStatusReads}");
                }

                if (busPerf.RelaxedTimer2Reads > 0) {
                    if (text[^1] != ' ') {
                        text.Append(' ');
                    }

                    text.Append($"t2Relax:{busPerf.RelaxedTimer2Reads}");
                }

                if (busPerf.MmioShadowHits > 0) {
                    if (text[^1] != ' ') {
                        text.Append(' ');
                    }

                    text.Append($"sh:{busPerf.MmioShadowHits}");
                }
            }

            return text.ToString();
        }

        private static void AppendMmioReadHotspot(StringBuilder text, uint addr, int count) {
            if (count <= 0) {
                return;
            }

            if (text.Length > "PSX poll ".Length) {
                text.Append(' ');
            }

            text.Append($"{FormatMmioReadAddress(addr)}x{count}");
        }

        private static string FormatMmioReadAddress(uint addr) {
            return addr switch {
                0x1F80_1814 => "gpu.stat",
                0x1F80_1810 => "gpu.read",
                0x1F80_1100 => "t0.val",
                0x1F80_1104 => "t0.mode",
                0x1F80_1108 => "t0.tgt",
                0x1F80_1110 => "t1.val",
                0x1F80_1114 => "t1.mode",
                0x1F80_1118 => "t1.tgt",
                0x1F80_1120 => "t2.val",
                0x1F80_1124 => "t2.mode",
                0x1F80_1128 => "t2.tgt",
                0x1F80_1070 => "istat",
                0x1F80_1074 => "imask",
                _ => $"0x{addr:x8}",
            };
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
            _perfBusFastReads += busPerf.ReadOpsFast;
            _perfBusMmioReads += busPerf.ReadOpsMmio;
            _perfBusFastWrites += busPerf.WriteOpsFast;
            _perfBusMmioWrites += busPerf.WriteOpsMmio;
            _perfInterruptNoPendingReturns += cpuPerf.InterruptNoPendingReturns;
            _perfInterruptMaskedReturns += cpuPerf.InterruptMaskedReturns;
            _perfInterruptGteDeferrals += cpuPerf.InterruptGteDeferrals;
            _perfInterruptExceptionsTaken += cpuPerf.InterruptExceptionsTaken;
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
            string mmioReadSummary = BuildMmioReadSummary(busPerf);

            _perfSummary =
                $"PSX core  cpu instr:{_perfCpuInstructions / frames:0} br:{_perfCpuBranches / frames:0} ld:{_perfCpuLoad8 / frames:0}/{_perfCpuLoad16 / frames:0}/{_perfCpuLoad32 / frames:0} st:{_perfCpuStore8 / frames:0}/{_perfCpuStore16 / frames:0}/{_perfCpuStore32 / frames:0} ic:{iCacheHitRate:0}%\n" +
                $"PSX mix  bus tick:{_perfBusTickCalls / frames:0.0} cyc:{_perfBusTickCycles / frames:0} fastR:{_perfBusFastReads / frames:0} mmioR:{_perfBusMmioReads / frames:0} fastW:{_perfBusFastWrites / frames:0} mmioW:{_perfBusMmioWrites / frames:0} irq:{_perfIrqChecks / frames:0.0}/{_perfInterruptHandles / frames:0.0} gpu tri:{_perfGpuTriangles / frames:0.0} rect:{_perfGpuRectangles / frames:0.0} line:{_perfGpuLines / frames:0.0} copy:{_perfGpuCopies / frames:0.0} spu samp:{_perfSpuMixedSamples / frames:0} actV:{avgActiveVoices:0.0}\n" +
                $"PSX irq  pend0:{_perfInterruptNoPendingReturns / frames:0.0} mask:{_perfInterruptMaskedReturns / frames:0.0} gte:{_perfInterruptGteDeferrals / frames:0.0} take:{_perfInterruptExceptionsTaken / frames:0.0}\n" +
                _syncGovernorSummary +
                (string.IsNullOrEmpty(mmioReadSummary) ? string.Empty : "\n" + mmioReadSummary);

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
            _perfInterruptNoPendingReturns = 0;
            _perfInterruptMaskedReturns = 0;
            _perfInterruptGteDeferrals = 0;
            _perfInterruptExceptionsTaken = 0;
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
