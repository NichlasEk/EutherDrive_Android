using ProjectPSX.Devices;
using ProjectPSX.Devices.CdRom;
using ProjectPSX.Devices.Expansion;
using ProjectPSX.Devices.Input;

namespace ProjectPSX {
    public class ProjectPSX {
        const int PSX_MHZ = 33868800;
        const int SYNC_CYCLES = 100;
        const int MIPS_UNDERCLOCK = 3; //Testing: This compensates the ausence of HALT instruction on MIPS Architecture, may broke some games.
        const int CYCLES_PER_FRAME = PSX_MHZ / 60;
        const int SYNC_LOOPS = (CYCLES_PER_FRAME / (SYNC_CYCLES * MIPS_UNDERCLOCK)) + 1;

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

        public ProjectPSX(IHostWindow window, string diskFilename, bool analogControllerEnabled = true, bool fastLoadEnabled = false) {
            controller = new DigitalController(analogControllerEnabled);
            memoryCard = new MemoryCard();

            interruptController = new InterruptController();

            cd = new CD(diskFilename);
            spu = new SPU(window, interruptController);
            gpu = new GPU(window);
            cdrom = new CDROM(cd, spu);
            cdrom.SetFastLoadEnabled(fastLoadEnabled);
            joypad = new JOYPAD(controller, memoryCard);
            timers = new TIMERS();
            mdec = new MDEC();
            exp2 = new Exp2();
            bus = new BUS(gpu, cdrom, spu, joypad, timers, mdec, interruptController, exp2);
            cpu = new CPU(bus);
            bus.SetRamWriteObserver(cpu.ObserveRamWrite);

            bus.loadBios();
            if (diskFilename.EndsWith(".exe")) {
                bus.loadEXE(diskFilename);
            }
        }

        public void RunFrame() {
            //A lame mainloop with a workaround to be able to underclock.
            int sync = 0;
            for (int i = 0; i < SYNC_LOOPS; i++) {
                while (sync < SYNC_CYCLES) {
                    int cpuCycles = cpu.Run();
                    sync += cpuCycles;
                    uint physicalPc = cpu.CurrentPC & 0x1FFF_FFFF;
                    if (!_psxBootBiosExited && physicalPc < 0x1FC0_0000 && physicalPc >= 0x0001_0000) {
                        _psxBootBiosExited = true;
                        cdrom.NotifyBiosExited();
                        cpu.NotifyBiosExited();
                    }
                    bus.tick(cpuCycles * MIPS_UNDERCLOCK);
                    cpu.handleInterrupts();
                }
                sync -= SYNC_CYCLES;
            }
        }

        public void JoyPadUp(GamepadInputsEnum button) => controller.handleJoyPadUp(button);

        public void JoyPadDown(GamepadInputsEnum button) => controller.handleJoyPadDown(button);

        public void SetAnalogControllerEnabled(bool enabled) => controller.SetAnalogControllerEnabled(enabled);

        public void SetFastLoadEnabled(bool enabled) => cdrom.SetFastLoadEnabled(enabled);

        public void toggleDebug() {
            cpu.debug = !cpu.debug;
            gpu.debug = !gpu.debug;
        }

        public void toggleCdRomLid() {
            cdrom.toggleLid();
        }

        public uint DebugCurrentPC => cpu.CurrentPC;

        public string DebugStartSummary() {
            return $"pc={cpu.CurrentPC:x8} biosExited={(_psxBootBiosExited ? 1 : 0)} {cdrom.DebugSummary()}";
        }

    }
}
