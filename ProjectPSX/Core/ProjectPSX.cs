using System;
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
        private static readonly int BusTickBatchCycles = ParseBusTickBatchCycles();

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

        public ProjectPSX(IHostWindow window, string diskFilename, bool analogControllerEnabled = true, bool fastLoadEnabled = false, bool superFastLoadEnabled = false) {
            controller = new DigitalController(analogControllerEnabled);
            memoryCard = new MemoryCard();

            interruptController = new InterruptController();

            cd = new CD(diskFilename);
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
                if (pendingBusCycles >= BusTickBatchCycles) {
                    bus.tick(pendingBusCycles);
                    pendingBusCycles = 0;
                    busFlushed = true;
                }

                if (busFlushed || bus.interruptController.interruptPending()) {
                    cpu.handleInterrupts();
                }
            }

            if (pendingBusCycles > 0) {
                bus.tick(pendingBusCycles);
                cpu.handleInterrupts();
            }
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
            uint sp = cpu.StackPointer;
            uint ra = cpu.ReturnAddress;
            uint loopDelta = v1 >= v0 ? v1 - v0 : 0;
            return $"pc={cpu.CurrentPC:x8} biosExited={(_psxBootBiosExited ? 1 : 0)} at={at:x8} v0={v0:x8} v1={v1:x8} dv={loopDelta:x8} a0={a0:x8} sp={sp:x8} ra={ra:x8} " +
                $"d370={d370:x8} d374={d374:x8} d378={d378:x8} " +
                $"irq=({interruptController.DebugISTAT:x3}/{interruptController.DebugIMASK:x3}) {bus.DMAController.DebugSummary(2)} {bus.DMAController.DebugSummary(3)} " +
                $"{gpu.DebugSummary()} {mdec.DebugSummary()} {cdrom.DebugSummary()}";
        }

        public string DebugCodeWindow(int wordsBefore = 8, int wordsAfter = 16) {
            uint pc = cpu.CurrentPC;
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

        private static int ParseBusTickBatchCycles() {
            const int fallback = 192;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PSX_BUS_TICK_BATCH_CYCLES");
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

    }
}
