using System;
using System.Diagnostics;
using System.Globalization;
using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.SegaCd;

namespace EutherDrive.Core.MdTracerCore
{
    internal static partial class md_main
    {
        private static readonly bool TraceConsoleEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CONSOLE"), "0", StringComparison.Ordinal)
            && !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_RAW_TIMING"), "1", StringComparison.Ordinal);
        private static readonly bool TraceShouldRun =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SHOULDRUN"), "1", StringComparison.Ordinal)
            && TraceConsoleEnabled;
        // ~7.670.453 Hz / 60 fps / 262 linjer ≈ 488
        public const int VDL_LINE_RENDER_MC68_CLOCK = 488;

        // Z80-klock per scanline (matchar originalkommentaren)
        public const int VDL_LINE_RENDER_Z80_CLOCK = 228;

        // --- Kärnkomponenter (ska bara definieras i EN md_main-partial i hela projektet) ---
        internal static md_cartridge? g_md_cartridge;
        internal static md_bus?       g_md_bus;
        internal static md_control?   g_md_control;
        internal static md_io?        g_md_io;        // om md_io ska vara instansklass
        internal static md_m68k?      g_md_m68k;
        internal static md_z80?       g_md_z80;
        internal static md_music?     g_md_music;     // ok även om stub

        // --- UI/Debug flaggor (ok att ligga här) ---
        public static bool g_screenA_enable, g_screenB_enable, g_screenW_enable, g_screenS_enable;
        public static bool g_pattern_enable, g_pallete_enable, g_code_enable;
        public static bool g_io_enable, g_music_enable, g_registry_enable, g_flow_enable;

        public static bool g_trace_fsb, g_trace_sip, g_hard_reset_req, g_trace_nextframe;

        // --- Trace-stub: behåll som objekt (inte bool), så opc-kod kan anropa metoder utan "bool.CPU_Trace_push" ---
        // OBS: Detta är inte samma sak som klassen Form_Code_Trace (som finns i Form_Code_Trace.cs).
        // Här pekar vi bara på en instans av den klassen (eller en dummy).
        public static Form_Code_Trace? g_form_code_trace;

        // Bakåtkompatibelt alias om gammal kod ropar md_main.runframe()
        public static void runframe() => RunFrame();

        // (valfritt) enkel indikator för UI
        private static int g_task_usage;
        private static int _z80ResetCycleId;
        internal static int Z80ResetCycleId => _z80ResetCycleId;
        internal static void BeginZ80ResetCycle()
        {
            unchecked
            {
                _z80ResetCycleId++;
            }
        }
        private static readonly double SmsCycleMultiplier = ParseSmsCycleMultiplier();
        private static double Z80CycleMultiplier = ParseZ80CycleMultiplier();
        
        // Fraktions-ackumulator för Z80→M68K cykelkonvertering
        // Ratio: M68K 7.67MHz / Z80 3.58MHz ≈ 767/358
        private static int _z80ToM68kRemainder;
        
        // Konvertera Z80-cykler till M68K-cykler med fraktions-ackumulator
        // Förhindrar att tiden står still när små Z80-slices konverteras till 0 M68K-cykler
        private static int ConvertZ80ToM68kCycles(int z80Cycles)
        {
            if (z80Cycles <= 0) return 0;
            
            // m68k = z80 * 767 / 358
            // Använd 64-bit för att undvika overflow
            long num = (long)z80Cycles * 767 + _z80ToM68kRemainder;
            int m68k = (int)(num / 358);
            _z80ToM68kRemainder = (int)(num % 358);
            
            // KRITISKT: Se till att minst 1 M68K-cykel alltid går framåt
            // Annars kan Z80 fastna i busy-loopar där tiden står still
            if (m68k == 0 && z80Cycles > 0)
            {
                m68k = 1;
                // Vi tar inte bort från remainder här, för att behålla korrekt fraktionsackumulering
            }
            
            // DEBUG: Log conversion for small values
            if (z80Cycles < 10 && MdLog.TraceZ80Win)
            {
                Console.WriteLine($"[Z80-CONV-DEBUG] z80={z80Cycles} m68k={m68k} remainder={_z80ToM68kRemainder} num={num}");
            }
            
            return m68k;
        }

        internal static long ConvertZ80ToM68kCyclesApprox(long z80Cycles)
        {
            if (z80Cycles <= 0)
                return 0;
            long m68k = (z80Cycles * 767L) / 358L;
            return m68k > 0 ? m68k : 1;
        }
        
        // DEBUG: Log SystemCycles advancement
        private static void DebugLogSystemCycles(string source, int z80Cycles, int m68kCycles)
        {
            if (!ReadEnvFlag("EUTHERDRIVE_TRACE_SYSTEM_CYCLES"))
                return;
            if (m68kCycles > 0)
                Console.Error.WriteLine($"[SYSTEM-CYCLES-DEBUG] {source}: z80={z80Cycles} m68k={m68kCycles} SystemCycles={SystemCycles}->{SystemCycles + m68kCycles}");
        }
        private static readonly bool ForceZ80RunPerLine = ReadEnvFlag("EUTHERDRIVE_Z80_RUN_PER_LINE");
        private static readonly int Z80RunPerLineFrames = ParseZ80RunPerLineFrames();
        private static readonly bool Z80RunBeforeM68k = ReadEnvFlag("EUTHERDRIVE_Z80_RUN_BEFORE_M68K");
        private static readonly int Z80InterleaveSlices = ParseZ80InterleaveSlices();
        private static readonly int Z80WaitBusReqFrames = ParseZ80WaitBusReqFrames();
        private static readonly int Z80WaitBusReqMaxFrames = ParseZ80WaitBusReqMaxFrames();
        private static readonly bool Z80WaitBusReqLog = ReadEnvFlag("EUTHERDRIVE_Z80_WAIT_BUSREQ_LOG");
        private static readonly bool TracePcPerFrame = ReadEnvFlag("EUTHERDRIVE_TRACE_PC_FRAME");
        private static readonly int TracePcEveryFrames = ParseNonNegativeInt("EUTHERDRIVE_TRACE_PC_FRAME_EVERY", 60);
        private static readonly bool TraceZ80FrameCycles = ReadEnvFlag("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES");
        private static readonly int TraceZ80FrameCyclesEvery = ParseNonNegativeInt("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES_EVERY", 60);
        private static readonly bool ForceSeega = ReadEnvFlag("EUTHERDRIVE_FORCE_SEEGA");
        private static readonly int ForceSeegaFrame = ParseNonNegativeInt("EUTHERDRIVE_FORCE_SEEGA_FRAME", 20);
        private static readonly uint ForceSeegaPtr = ParseForceSeegaPtr();
        private static readonly bool TraceSonic2Ram = ReadEnvFlag("EUTHERDRIVE_TRACE_SONIC2_RAM");
        private static readonly int TraceSonic2RamLimit = ParseNonNegativeInt("EUTHERDRIVE_TRACE_SONIC2_RAM_LIMIT", 512);
        private static int _traceSonic2RamRemaining = TraceSonic2Ram ? TraceSonic2RamLimit : 0;
        private static bool _sonic2RamInit;
        private static readonly byte[] _sonic2RamLast = new byte[0x80];
        private static readonly bool UseM68kEmuMain =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_USE_M68KEMU"), "0", StringComparison.Ordinal);
        private static M68000? _m68kEmu;
        private static IBusInterface? _m68kEmuBus;
        private static int _m68kWaitCycles;
        private static int _m68kRefreshCounter;
        // Global deadlock safeguard for M68KEmu path:
        // if IRQ is pending while mask stays at 7 for too long, release to level 3.
        // Enabled by default; set EUTHERDRIVE_M68K_IRQ_AUTOUNMASK=0 to disable.
        private static readonly bool AutoUnmaskIrqDebug =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_IRQ_AUTOUNMASK"), "0", StringComparison.Ordinal);
        private static int _autoUnmaskStuckCount;
        private static bool _autoUnmaskLogged;
        private static long _autoUnmaskFireCount;
        private static int _m68kEmuLastSliceExecInstructions;
        private static int _m68kEmuLastSliceDmaWaitCycles;
        private static int _m68kEmuLastSliceDmaWaitEvents;
        private static int _m68kEmuLastSliceRefreshWaitCycles;
        private static int _m68kEmuLastSliceRefreshWaitEvents;
        private static int _m68kEmuNoExecSliceStreak;
        private static long _runFrameEnterCount;
        private static long _runFrameCompleteCount;
        private static long _runFrameLastFrame;
        private static int _runFrameLastLines;
        private static int _runFrameLastM68kCalls;
        private static int _runFrameLastM68kBudget;
        private static bool _loadedM68kEmuStateFromSavestate;
        // Z80/M68K cycle ratio for NTSC: Z80 ~3.58MHz, M68K ~7.67MHz => ratio ~0.466
        private static readonly double Z80PerM68kRatio = 3.579545 / 7.670000; // More precise ratio
        private static readonly int Z80ContinuousSliceCycles = ParseZ80ContinuousSliceCycles();
        private static bool UseZ80ContinuousScheduling => !g_masterSystemMode && Z80ContinuousSliceCycles > 0;
        private static int SmsCyclesPerLine => Math.Max(1, (int)(VDL_LINE_RENDER_Z80_CLOCK * SmsCycleMultiplier));
        private static int Z80CyclesPerLine => Math.Max(1, (int)(VDL_LINE_RENDER_Z80_CLOCK * Z80CycleMultiplier));
        public static int GetZ80CyclesPerLine() => Z80CyclesPerLine;
        public static int GetSmsCyclesPerLine() => SmsCyclesPerLine;

        internal static void SetZ80CycleMultiplier(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                return;
            Z80CycleMultiplier = value;
        }
        private static readonly Stopwatch _smsCycleLogTimer = Stopwatch.StartNew();
        private static long _smsCycleLogLastMs;
        private static long _smsCycleLogAccumBudget;
        private static long _smsCycleLogAccumActual;
        private const int SmsCycleLogIntervalMs = 1000;
        private static bool _z80FrameScheduleLogged;
         private static int _z80WaitFrames;
        private static int _z80StableLowFrames;
        private static bool _z80WaitReleased;
        private static int _z80SliceCount;
        private static int _z80TotalCycles;
        private static int _z80MaxSlice;
        private static double _z80ContinuousBudgetCarry;
        
        public static int Z80SliceCount 
        { 
            get => _z80SliceCount;
            set => _z80SliceCount = value;
        }
        
        public static int Z80TotalCycles 
        { 
            get => _z80TotalCycles;
            set => _z80TotalCycles = value;
        }
        
        public static int Z80MaxSlice 
        { 
            get => _z80MaxSlice;
            set => _z80MaxSlice = value;
        }
        
        public static void ResetZ80Telemetry()
        {
            _z80SliceCount = 0;
            _z80TotalCycles = 0;
            _z80MaxSlice = 0;
        }
        
        public static void AddZ80Cycles(int cycles)
        {
            _z80TotalCycles += cycles;
            _z80SliceCount++;
            if (cycles > _z80MaxSlice)
                _z80MaxSlice = cycles;
        }
        
        // ALADDIN DEBUG: Track IRQs and timer events
        private static int _z80IrqCount;
        private static int _ymTimerAOverflows;
        private static int _ymTimerBOverflows;
        private static int _ymBusySets;
        private static int _ymBusyClears;
        private static int _m68kCyclesThisFrame;
        private static int _timerControlCalls;
        
        // Per-frame diagnostics
        private static long _lastDiagnosticFrame = -1;
        private static int _z80CyclesLastFrame;
        private static int _ymAdvanceCallsLastFrame;
        private static int _ymSamplesProducedLastFrame;
        private static int _psgAdvanceCallsLastFrame;
        
        public static int Z80IrqCount => _z80IrqCount;
        public static int YmTimerAOverflows => _ymTimerAOverflows;
        public static int YmTimerBOverflows => _ymTimerBOverflows;
        public static int YmBusySets => _ymBusySets;
        public static int YmBusyClears => _ymBusyClears;
        public static int TimerControlCalls => _timerControlCalls;
        
        public static void ResetAladdinDebug()
        {
            _z80IrqCount = 0;
            _ymTimerAOverflows = 0;
            _ymTimerBOverflows = 0;
            _ymBusySets = 0;
            _ymBusyClears = 0;
        }
        
        public static void IncrementZ80Irq() => _z80IrqCount++;
        public static void IncrementYmTimerAOverflow() => _ymTimerAOverflows++;
        public static void IncrementYmTimerBOverflow() => _ymTimerBOverflows++;
        public static void IncrementYmBusySet() => _ymBusySets++;
        public static void IncrementYmBusyClear() => _ymBusyClears++;
        public static void AddM68kCycles(int cycles) => _m68kCyclesThisFrame += cycles;
        public static void IncrementTimerControlCalls() => _timerControlCalls++;
        public static void IncrementYmAdvanceCalls() => _ymAdvanceCallsLastFrame++;
        public static void IncrementYmAdvanceCallsBy(int count) => _ymAdvanceCallsLastFrame += count;
        
        // Public accessors for DIAG-FRAME logging
        public static int M68kCyclesThisFrame => _m68kCyclesThisFrame;
        public static int YmAdvanceCallsLastFrame => _ymAdvanceCallsLastFrame;
        public static void ResetYmAdvanceCalls() => _ymAdvanceCallsLastFrame = 0;
        public static void ResetM68kCyclesThisFrame() => _m68kCyclesThisFrame = 0;
        private static bool _z80WaitLogged;
        private static bool _mbxInjected;
        private static bool _mbxInjectAcked;
        private static bool _mbxInjectPendingClear;
        private static bool _mbxInjectCleared;
        private static bool _mbxInjectArmedLogged;
        private static bool _mbxInjectEnvLogged;
        private static bool _mbxInjectConfigLoaded;
        private static bool _forceSeegaDone;
        private static ushort _injectMbxAddr;
        private static byte _injectMbxValue;
        private static long _injectMbxFrame;

        // --- System-wide monotonic cycle counter for YM2612 timing ---
        // This counter advances as the system executes, providing a monotonic
        // timebase for YM2612 busy checks that doesn't depend on per-frame budgets.
        // We use M68K cycles as the base unit (VDL_LINE_RENDER_MC68_CLOCK per line).
        private static long _systemCycles;
        private static long _mainHintReqCount;
        private static long _mainVintReqCount;
        private static long _mainExtReqCount;
        private static long _mainHintAckCount;
        private static long _mainVintAckCount;
        private static long _mainExtAckCount;
        internal static long SystemCycles => _systemCycles;
        internal static bool MainM68kEmuConfigured => UseM68kEmuMain;
        internal static bool MainM68kEmuReady => UseM68kEmuMain && _m68kEmu != null && _m68kEmuBus != null;
        internal static void AdvanceSystemCycles(long cycles)
        {
            if (cycles <= 0)
                return;
            _systemCycles += cycles;
            g_md_vdp?.ProcessVdpFifoForM68kCycles((int)Math.Min(cycles, int.MaxValue));
            g_md_music?.YmAdvanceSystemCycles(cycles);
        }

        internal static void AddM68kWaitCycles(int cycles)
        {
            if (cycles <= 0)
                return;
            if (UseM68kEmuMain)
            {
                _m68kWaitCycles += cycles;
            }
            else
            {
                md_m68k.g_clock += cycles;
            }
        }

        internal readonly struct MainInterruptDebugSnapshot
        {
            internal readonly bool UsingM68kEmu;
            internal readonly bool M68kEmuConfigured;
            internal readonly bool M68kEmuReady;
            internal readonly ushort Sr;
            internal readonly byte InterruptMask;
            internal readonly int PendingInterruptLevel;
            internal readonly byte BusInterruptLevel;
            internal readonly bool WillTakeInterrupt;
            internal readonly bool CpuStopped;
            internal readonly bool CpuFrozen;
            internal readonly ushort NextOpcode;
            internal readonly bool HintReq;
            internal readonly bool VintReq;
            internal readonly bool ExtReq;
            internal readonly bool HintEnabled;
            internal readonly bool VintEnabled;
            internal readonly long HintReqCount;
            internal readonly long VintReqCount;
            internal readonly long ExtReqCount;
            internal readonly long HintAckCount;
            internal readonly long VintAckCount;
            internal readonly long ExtAckCount;
            internal readonly int M68kEmuSliceExecInstructions;
            internal readonly int M68kEmuSliceDmaWaitCycles;
            internal readonly int M68kEmuSliceDmaWaitEvents;
            internal readonly int M68kEmuSliceRefreshWaitCycles;
            internal readonly int M68kEmuSliceRefreshWaitEvents;
            internal readonly int M68kEmuNoExecSliceStreak;
            internal readonly long AutoUnmaskFireCount;
            internal readonly long RunFrameEnterCount;
            internal readonly long RunFrameCompleteCount;
            internal readonly long RunFrameLastFrame;
            internal readonly int RunFrameLastLines;
            internal readonly int RunFrameLastM68kCalls;
            internal readonly int RunFrameLastM68kBudget;

            internal MainInterruptDebugSnapshot(
                bool usingM68kEmu,
                bool m68kEmuConfigured,
                bool m68kEmuReady,
                ushort sr,
                byte interruptMask,
                int pendingInterruptLevel,
                byte busInterruptLevel,
                bool willTakeInterrupt,
                bool cpuStopped,
                bool cpuFrozen,
                ushort nextOpcode,
                bool hintReq,
                bool vintReq,
                bool extReq,
                bool hintEnabled,
                bool vintEnabled,
                long hintReqCount,
                long vintReqCount,
                long extReqCount,
                long hintAckCount,
                long vintAckCount,
                long extAckCount,
                int m68kEmuSliceExecInstructions,
                int m68kEmuSliceDmaWaitCycles,
                int m68kEmuSliceDmaWaitEvents,
                int m68kEmuSliceRefreshWaitCycles,
                int m68kEmuSliceRefreshWaitEvents,
                int m68kEmuNoExecSliceStreak,
                long autoUnmaskFireCount,
                long runFrameEnterCount,
                long runFrameCompleteCount,
                long runFrameLastFrame,
                int runFrameLastLines,
                int runFrameLastM68kCalls,
                int runFrameLastM68kBudget)
            {
                UsingM68kEmu = usingM68kEmu;
                M68kEmuConfigured = m68kEmuConfigured;
                M68kEmuReady = m68kEmuReady;
                Sr = sr;
                InterruptMask = interruptMask;
                PendingInterruptLevel = pendingInterruptLevel;
                BusInterruptLevel = busInterruptLevel;
                WillTakeInterrupt = willTakeInterrupt;
                CpuStopped = cpuStopped;
                CpuFrozen = cpuFrozen;
                NextOpcode = nextOpcode;
                HintReq = hintReq;
                VintReq = vintReq;
                ExtReq = extReq;
                HintEnabled = hintEnabled;
                VintEnabled = vintEnabled;
                HintReqCount = hintReqCount;
                VintReqCount = vintReqCount;
                ExtReqCount = extReqCount;
                HintAckCount = hintAckCount;
                VintAckCount = vintAckCount;
                ExtAckCount = extAckCount;
                M68kEmuSliceExecInstructions = m68kEmuSliceExecInstructions;
                M68kEmuSliceDmaWaitCycles = m68kEmuSliceDmaWaitCycles;
                M68kEmuSliceDmaWaitEvents = m68kEmuSliceDmaWaitEvents;
                M68kEmuSliceRefreshWaitCycles = m68kEmuSliceRefreshWaitCycles;
                M68kEmuSliceRefreshWaitEvents = m68kEmuSliceRefreshWaitEvents;
                M68kEmuNoExecSliceStreak = m68kEmuNoExecSliceStreak;
                AutoUnmaskFireCount = autoUnmaskFireCount;
                RunFrameEnterCount = runFrameEnterCount;
                RunFrameCompleteCount = runFrameCompleteCount;
                RunFrameLastFrame = runFrameLastFrame;
                RunFrameLastLines = runFrameLastLines;
                RunFrameLastM68kCalls = runFrameLastM68kCalls;
                RunFrameLastM68kBudget = runFrameLastM68kBudget;
            }
        }

        internal static MainInterruptDebugSnapshot CaptureMainInterruptDebug()
        {
            md_vdp? vdp = g_md_vdp;
            bool hintReq = md_m68k.g_interrupt_H_req;
            bool vintReq = md_m68k.g_interrupt_V_req;
            bool extReq = md_m68k.g_interrupt_EXT_req;
            bool hintEnabled = vdp != null && vdp.g_vdp_reg_0_4_hinterrupt == 1;
            bool vintEnabled = vdp != null && vdp.g_vdp_reg_1_5_vinterrupt == 1;

            if (UseM68kEmuMain && _m68kEmu != null)
            {
                ushort sr = _m68kEmu.StatusRegister;
                byte mask = (byte)((sr >> 8) & 0x07);
                int pending = _m68kEmu.PendingInterruptLevel.HasValue ? _m68kEmu.PendingInterruptLevel.Value : -1;
                byte busLevel = _m68kEmuBus?.InterruptLevel() ?? 0;
                bool willTake = pending >= 0 || busLevel > mask;
                return new MainInterruptDebugSnapshot(
                    usingM68kEmu: true,
                    m68kEmuConfigured: true,
                    m68kEmuReady: true,
                    sr: sr,
                    interruptMask: mask,
                    pendingInterruptLevel: pending,
                    busInterruptLevel: busLevel,
                    willTakeInterrupt: willTake,
                    cpuStopped: _m68kEmu.IsStopped,
                    cpuFrozen: _m68kEmu.IsFrozen,
                    nextOpcode: _m68kEmu.NextOpcode,
                    hintReq: hintReq,
                    vintReq: vintReq,
                    extReq: extReq,
                    hintEnabled: hintEnabled,
                    vintEnabled: vintEnabled,
                    hintReqCount: _mainHintReqCount,
                    vintReqCount: _mainVintReqCount,
                    extReqCount: _mainExtReqCount,
                    hintAckCount: _mainHintAckCount,
                    vintAckCount: _mainVintAckCount,
                    extAckCount: _mainExtAckCount,
                    m68kEmuSliceExecInstructions: _m68kEmuLastSliceExecInstructions,
                    m68kEmuSliceDmaWaitCycles: _m68kEmuLastSliceDmaWaitCycles,
                    m68kEmuSliceDmaWaitEvents: _m68kEmuLastSliceDmaWaitEvents,
                    m68kEmuSliceRefreshWaitCycles: _m68kEmuLastSliceRefreshWaitCycles,
                    m68kEmuSliceRefreshWaitEvents: _m68kEmuLastSliceRefreshWaitEvents,
                    m68kEmuNoExecSliceStreak: _m68kEmuNoExecSliceStreak,
                    autoUnmaskFireCount: _autoUnmaskFireCount,
                    runFrameEnterCount: _runFrameEnterCount,
                    runFrameCompleteCount: _runFrameCompleteCount,
                    runFrameLastFrame: _runFrameLastFrame,
                    runFrameLastLines: _runFrameLastLines,
                    runFrameLastM68kCalls: _runFrameLastM68kCalls,
                    runFrameLastM68kBudget: _runFrameLastM68kBudget);
            }

            ushort srLegacy = md_m68k.g_reg_SR;
            byte maskLegacy = (byte)(md_m68k.g_status_interrupt_mask & 0x07);
            byte busLevelLegacy = 0;
            if (hintReq && hintEnabled)
                busLevelLegacy = 4;
            else if (vintReq && vintEnabled && !md_m68k.g_interrupt_H_act)
                busLevelLegacy = 6;
            else if (extReq)
                busLevelLegacy = md_m68k.g_interrupt_EXT_level;

            bool willTakeLegacy = busLevelLegacy > maskLegacy;
            return new MainInterruptDebugSnapshot(
                usingM68kEmu: false,
                m68kEmuConfigured: UseM68kEmuMain,
                m68kEmuReady: false,
                sr: srLegacy,
                interruptMask: maskLegacy,
                pendingInterruptLevel: -1,
                busInterruptLevel: busLevelLegacy,
                willTakeInterrupt: willTakeLegacy,
                cpuStopped: md_m68k.g_68k_stop,
                cpuFrozen: false,
                nextOpcode: md_m68k.g_opcode,
                hintReq: hintReq,
                vintReq: vintReq,
                extReq: extReq,
                hintEnabled: hintEnabled,
                vintEnabled: vintEnabled,
                hintReqCount: _mainHintReqCount,
                vintReqCount: _mainVintReqCount,
                extReqCount: _mainExtReqCount,
                hintAckCount: _mainHintAckCount,
                vintAckCount: _mainVintAckCount,
                extAckCount: _mainExtAckCount,
                m68kEmuSliceExecInstructions: 0,
                m68kEmuSliceDmaWaitCycles: 0,
                m68kEmuSliceDmaWaitEvents: 0,
                m68kEmuSliceRefreshWaitCycles: 0,
                m68kEmuSliceRefreshWaitEvents: 0,
                m68kEmuNoExecSliceStreak: 0,
                autoUnmaskFireCount: _autoUnmaskFireCount,
                runFrameEnterCount: _runFrameEnterCount,
                runFrameCompleteCount: _runFrameCompleteCount,
                runFrameLastFrame: _runFrameLastFrame,
                runFrameLastLines: _runFrameLastLines,
                runFrameLastM68kCalls: _runFrameLastM68kCalls,
                runFrameLastM68kBudget: _runFrameLastM68kBudget);
        }

        internal static void EnsureMainM68kBackend()
        {
            if (!UseM68kEmuMain)
                return;
            if (g_md_bus == null)
                return;

            _m68kEmu ??= M68000.CreateBuilder()
                .AllowTasWrites(md_m68k.AllowTasWrites)
                .Name("MD-MAIN")
                .Build();
            _m68kEmuBus = new SegaCdMainM68kBus(g_md_bus);
            _m68kEmu.Reset(_m68kEmuBus);
            _m68kWaitCycles = 0;
            _m68kRefreshCounter = 0;
            md_m68k.g_reg_PC = _m68kEmu.Pc;
            md_m68k.g_opcode = _m68kEmu.NextOpcode;
        }

        internal static void SyncM68kEmuFromLegacyState()
        {
            if (!UseM68kEmuMain)
                return;

            EnsureMainM68kBackend();
            if (_m68kEmu == null)
                return;

            uint[] data = new uint[8];
            for (int i = 0; i < 8; i++)
                data[i] = md_m68k.g_reg_data[i].l;

            uint[] address = new uint[7];
            for (int i = 0; i < 7; i++)
                address[i] = md_m68k.g_reg_addr[i].l;

            ushort sr = md_m68k.g_reg_SR;
            bool supervisor = (sr & 0x2000) != 0;
            uint a7 = md_m68k.g_reg_addr[7].l;
            uint usp = supervisor ? md_m68k.g_reg_addr_usp.l : a7;
            uint ssp = supervisor ? a7 : md_m68k.g_reg_addr_usp.l;

            var state = new M68000.M68000State(
                data: data,
                address: address,
                usp: usp,
                ssp: ssp,
                sr: sr,
                pc: md_m68k.g_reg_PC,
                prefetch: md_m68k.g_opcode);
            _m68kEmu.SetState(state);

            _m68kWaitCycles = 0;
            _m68kRefreshCounter = 0;
        }

        internal static void FinalizeM68kStateAfterSavestateLoad()
        {
            if (!UseM68kEmuMain)
                return;

            if (_loadedM68kEmuStateFromSavestate)
            {
                if (_m68kEmu != null)
                {
                    md_m68k.g_reg_PC = _m68kEmu.Pc;
                    md_m68k.g_opcode = _m68kEmu.NextOpcode;
                }
                return;
            }

            SyncM68kEmuFromLegacyState();
        }

        internal static void CountMainIrqRequest(byte level)
        {
            switch (level)
            {
                case 4:
                    _mainHintReqCount++;
                    break;
                case 6:
                    _mainVintReqCount++;
                    break;
                default:
                    if (level != 0)
                        _mainExtReqCount++;
                    break;
            }
        }

        internal static void CountMainIrqAcknowledge(byte level)
        {
            switch (level)
            {
                case 4:
                    _mainHintAckCount++;
                    break;
                case 6:
                    _mainVintAckCount++;
                    break;
                default:
                    if (level != 0)
                        _mainExtAckCount++;
                    break;
            }
        }

        private static void ResetMainIrqDebugCounters()
        {
            _mainHintReqCount = 0;
            _mainVintReqCount = 0;
            _mainExtReqCount = 0;
            _mainHintAckCount = 0;
            _mainVintAckCount = 0;
            _mainExtAckCount = 0;
            _m68kEmuLastSliceExecInstructions = 0;
            _m68kEmuLastSliceDmaWaitCycles = 0;
            _m68kEmuLastSliceDmaWaitEvents = 0;
            _m68kEmuLastSliceRefreshWaitCycles = 0;
            _m68kEmuLastSliceRefreshWaitEvents = 0;
            _m68kEmuNoExecSliceStreak = 0;
            _autoUnmaskFireCount = 0;
            _runFrameEnterCount = 0;
            _runFrameCompleteCount = 0;
            _runFrameLastFrame = -1;
            _runFrameLastLines = 0;
            _runFrameLastM68kCalls = 0;
            _runFrameLastM68kBudget = 0;
        }

        // Synkroniseringssystem för gemensam tidsbas (inspirerat av andra emulatorer)
        internal class SyncState
        {
            public long CurrentCycle;
        }

        // Synkroniseringsstatus för varje komponent
        private static SyncState _syncFm = new SyncState();
        private static SyncState _syncPsg = new SyncState();
        private static SyncState _syncZ80 = new SyncState();
        private static SyncState _syncM68k = new SyncState();
        
        internal static SyncState GetSyncFm() => _syncFm;

        internal static void SyncZ80ToSystemCycles()
        {
            if (g_md_z80 == null)
                return;
            if (g_md_bus != null && (g_md_bus.Z80BusGranted || g_md_bus.Z80Reset))
                return;

            // In continuous scheduler mode Z80 is already budgeted proportionally to
            // M68K execution. Running an additional catch-up here can double-count Z80
            // time during frequent BUSREQ toggles (observed as ~2x Z80 cycles/sec).
            if (UseZ80ContinuousScheduling)
            {
                _syncZ80.CurrentCycle = SystemCycles;
                return;
            }

            long target = SystemCycles;
            long delta = target - _syncZ80.CurrentCycle;
            if (delta <= 0)
            {
                _syncZ80.CurrentCycle = target;
                return;
            }
            int z80Cycles = (int)Math.Round(delta * Z80PerM68kRatio);
            if (z80Cycles <= 0)
            {
                _syncZ80.CurrentCycle = target;
                return;
            }
            _syncZ80.CurrentCycle = target;
            g_md_z80.run(z80Cycles);
        }

        // Gemensam tidsbas - mastercykler (högsta precision)
        // Använder samma som SystemCycles för att undvika timing-konflikter
        private static int _syncCommonDebugCount;

        // Synkroniseringsfunktioner
        internal static long SyncCommon(SyncState sync, long targetCycle, int clockDivisor = 1)
        {
            long nativeTargetCycle = targetCycle / clockDivisor;
            long cyclesToDo = nativeTargetCycle - sync.CurrentCycle;
            
            // DEBUG: Log first few sync calls
            if (_syncCommonDebugCount < 10)
            {
                _syncCommonDebugCount++;
                Console.WriteLine($"[SYNC-COMMON-DEBUG] call#{_syncCommonDebugCount}: sync.CurrentCycle={sync.CurrentCycle} targetCycle={targetCycle} nativeTargetCycle={nativeTargetCycle} cyclesToDo={cyclesToDo}");
            }
            
            if (cyclesToDo < 0)
            {
                // Should not happen if properly synchronized
                cyclesToDo = 0;
            }
            
            sync.CurrentCycle = nativeTargetCycle;
            return cyclesToDo;
        }

        // Hämta aktuell mastercykel
        internal static long GetMasterCycle() => SystemCycles;

        // Öka mastercykler (anropas när någon CPU kör)
        internal static void AdvanceMasterCycles(long cycles)
        {
            AdvanceSystemCycles(cycles);
            
            // DEBUG: Log first few advances
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MASTER_CYCLES") == "1")
            {
                Console.WriteLine($"[MASTER-CYCLES-DEBUG] +{cycles} = {SystemCycles}");
            }
        }

        private static void RunM68kWithVdp(int cycles)
        {
            if (cycles <= 0)
                return;
            if (UseM68kEmuMain)
            {
                RunM68kEmu(cycles);
                return;
            }
            g_md_m68k.run(cycles);
            AdvanceSystemCycles(cycles);
        }

        private static void RunM68kEmu(int cycles)
        {
            if (_m68kEmu == null || _m68kEmuBus == null)
                return;

            // Keep legacy md_m68k clocks slice-local while running the new core.
            // This avoids int overflow corrupting HV timing probes after long runs.
            md_m68k.g_slice_start_clock_total = 0;
            md_m68k.g_slice_clock_len = cycles;
            md_m68k.g_clock_now = 0;
            md_m68k.g_clock_total = cycles;

            int remaining = cycles;
            int guard = 0;
            int execInstructions = 0;
            int dmaWaitCycles = 0;
            int dmaWaitEvents = 0;
            int refreshWaitCycles = 0;
            int refreshWaitEvents = 0;
            while (remaining > 0)
            {
                if (++guard > 200000)
                    break;

                if (AutoUnmaskIrqDebug)
                {
                    byte busLevelNow = _m68kEmuBus.InterruptLevel();
                    byte maskNow = _m68kEmu.InterruptPriorityMask;
                    if (busLevelNow != 0 && maskNow >= 7)
                    {
                        _autoUnmaskStuckCount++;
                        if (_autoUnmaskStuckCount > 32)
                        {
                            _m68kEmu.ForceInterruptMask(3);
                            _autoUnmaskFireCount++;
                            if (!_autoUnmaskLogged)
                            {
                                _autoUnmaskLogged = true;
                                Console.WriteLine(
                                    $"[M68K-AUTOUNMASK] pc=0x{_m68kEmu.Pc:X8} bus={busLevelNow} oldMask={maskNow} -> newMask=3");
                            }
                            _autoUnmaskStuckCount = 0;
                        }
                    }
                    else
                    {
                        _autoUnmaskStuckCount = 0;
                    }
                }

                if (g_md_vdp != null)
                {
                    int dmaWait = g_md_vdp.dma_status_update();
                    if (dmaWait > 0)
                    {
                        int waitStep = Math.Min(remaining, dmaWait);
                        dmaWaitEvents++;
                        dmaWaitCycles += waitStep;
                        AddM68kCycles(waitStep);
                        AdvanceSystemCycles(waitStep);
                        md_m68k.g_clock_now += waitStep;
                        remaining -= waitStep;
                        continue;
                    }
                }

                if (_m68kWaitCycles > 0)
                {
                    int waitStep = Math.Min(remaining, _m68kWaitCycles);
                    refreshWaitEvents++;
                    refreshWaitCycles += waitStep;
                    _m68kWaitCycles -= waitStep;
                    md_m68k.g_clock_now += waitStep;
                    AddM68kCycles(waitStep);
                    AdvanceSystemCycles(waitStep);
                    remaining -= waitStep;
                    continue;
                }

                md_m68k.g_reg_PC = _m68kEmu.Pc;
                md_m68k.g_opcode = _m68kEmu.NextOpcode;
                execInstructions++;

                uint used = _m68kEmu.ExecuteInstruction(_m68kEmuBus);
                if (used == 0)
                    used = 1;

                md_m68k.g_reg_PC = _m68kEmu.Pc;
                md_m68k.g_opcode = _m68kEmu.NextOpcode;

                AddM68kCycles((int)used);
                AdvanceSystemCycles(used);
                md_m68k.g_clock_now += (int)used;
                remaining -= (int)used;

                // Approximate 68k refresh wait cycles (jgenesis timing.rs)
                _m68kRefreshCounter += (int)used;
                if (_m68kRefreshCounter >= 128)
                {
                    int regionIdx = (int)((_m68kEmu.Pc >> 21) & 7);
                    int waitCycles = regionIdx switch
                    {
                        0 or 1 or 2 or 3 => 2,
                        7 => 3,
                        _ => 0
                    };
                    _m68kWaitCycles += waitCycles;
                    _m68kRefreshCounter %= 128;
                }
            }

            _m68kEmuLastSliceExecInstructions = execInstructions;
            _m68kEmuLastSliceDmaWaitCycles = dmaWaitCycles;
            _m68kEmuLastSliceDmaWaitEvents = dmaWaitEvents;
            _m68kEmuLastSliceRefreshWaitCycles = refreshWaitCycles;
            _m68kEmuLastSliceRefreshWaitEvents = refreshWaitEvents;
            _m68kEmuNoExecSliceStreak = execInstructions == 0
                ? (_m68kEmuNoExecSliceStreak + 1)
                : 0;
        }

        // --- Kärnkomponenter ---
        internal static md_vdp g_md_vdp = new md_vdp();


        /// <summary>
        /// Init + ROM-load + reset. Ingen evighetsloop här.
        /// UI (Avalonia) kallar RunFrame() per frame/tick (~60 Hz).
        /// </summary>
        public static bool run(string romPath)
        {
            // Skapa instanser EN gång (lazy init)
            g_md_cartridge ??= new md_cartridge();
            g_md_bus       ??= new md_bus();
            g_md_control   ??= new md_control();

            // Om md_io är en instansklass (INTE static class). Om den råkar vara static: ta bort denna raden + alla g_md_io-användningar.
            g_md_io = new md_io();
            md_io.Current = g_md_io;

            g_md_m68k      ??= new md_m68k();
            g_md_z80       ??= new md_z80();
            g_md_music     ??= new md_music();

            // Trace-instans om den finns i projektet (Form_Code_Trace.cs)
            g_form_code_trace ??= new Form_Code_Trace();

            // Ladda ROM
            if (!g_md_cartridge.load(romPath))
                return false;

            // Reset maskinen
            g_md_m68k.reset();
            ResetMainIrqDebugCounters();
            if (UseM68kEmuMain)
            {
                EnsureMainM68kBackend();
            }
            SafeResetZ80();
            g_md_vdp.reset();
            _mbxInjected = false;
            _mbxInjectAcked = false;
            _mbxInjectPendingClear = false;
            _mbxInjectCleared = false;
            _mbxInjectArmedLogged = false;
            _mbxInjectEnvLogged = false;
            _mbxInjectConfigLoaded = false;
            ResetZ80WaitState();

            g_hard_reset_req  = false;
            g_trace_nextframe = false;

            return true;
        }

        internal static void EnsureCpuStubs()
        {
            // TODO: koppla riktig CPU/Bus senare.
        }


        /// <summary>
        /// Kör en hel bildruta (alla VDP-scanlines) i headless-läge.
        /// </summary>
        public static void RunFrame()
        {
            // Säkerställ att run() körts
            if (g_md_m68k is null || g_md_z80 is null)
                return;
            _runFrameEnterCount++;

            if (g_hard_reset_req)
            {
                g_md_m68k.reset();
                ResetMainIrqDebugCounters();
                if (UseM68kEmuMain)
                    EnsureMainM68kBackend();
                SafeResetZ80();
                g_md_vdp.reset();
                ResetZ80WaitState();
                g_hard_reset_req = false;
                _mbxInjected = false;
                _mbxInjectAcked = false;
                _mbxInjectPendingClear = false;
                _mbxInjectCleared = false;
                _mbxInjectArmedLogged = false;
                _mbxInjectEnvLogged = false;
                _mbxInjectConfigLoaded = false;
            }

            int lines = g_md_vdp.g_vertical_line_max;
            long frame = g_md_vdp?.FrameCounter ?? -1;
            int frameM68kCalls = 0;
            int frameM68kBudget = 0;
            if (ForceSeega && !_forceSeegaDone && frame >= ForceSeegaFrame)
            {
                md_m68k.write32(0xFFF680, ForceSeegaPtr);
                md_m68k.write16(0xFFF6F8, 0x0000);
                Console.WriteLine($"[SEEGA-INJECT] frame={frame} ptr=0x{ForceSeegaPtr:X8}");
                _forceSeegaDone = true;
            }
            if (TracePcPerFrame && TracePcEveryFrames >= 0)
            {
                if (TracePcEveryFrames == 0 || (frame >= 0 && frame % TracePcEveryFrames == 0))
                    Console.WriteLine($"[PCFRAME] frame={frame} pc=0x{md_m68k.g_reg_PC:X6}");
            }
            
            // DEBUG: Log frame number (gated)
            if (ReadEnvFlag("EUTHERDRIVE_TRACE_FRAME_DEBUG") && frame >= 10 && frame <= 20)
                Console.WriteLine($"[FRAME-DEBUG] RunFrame called with frame={frame}");
            
            // CRITICAL: Tick Z80 safe boot timer BEFORE Z80 runs
            // This allows Z80 to run immediately when reset is released
            g_md_bus?.TickZ80SafeBoot(frame);
            g_md_bus?.ApplyZ80BusReqLatch();
            
            int z80LineBudget = g_masterSystemMode ? SmsCyclesPerLine : Z80CyclesPerLine;
            bool z80RunPerLine = g_masterSystemMode;
            if (!g_masterSystemMode && ForceZ80RunPerLine)
            {
                z80RunPerLine = Z80RunPerLineFrames <= 0 || (frame >= 0 && frame < Z80RunPerLineFrames);
            }
             bool allowZ80 = ShouldRunZ80(frame);
             
            // DEBUG: Log Z80 scheduling (gated)
            if (ReadEnvFlag("EUTHERDRIVE_TRACE_Z80_SCHED_DEBUG") && frame >= 10 && frame <= 20)
            {
                bool debugBusReq = g_md_bus?.Z80BusGranted ?? false;
                bool debugReset = g_md_bus?.Z80Reset ?? false;
                Console.WriteLine($"[Z80-SCHED-DEBUG] frame={frame} allowZ80={allowZ80} z80RunPerLine={z80RunPerLine} UseZ80ContinuousScheduling={UseZ80ContinuousScheduling} busReq={debugBusReq} reset={debugReset}");
            }
            int z80FrameBudget = z80LineBudget * lines;
             if (UseZ80ContinuousScheduling && !_z80FrameScheduleLogged)
            {
                _z80FrameScheduleLogged = true;
                Console.WriteLine($"[Z80SCHED] mode=continuous sliceM68k={Z80ContinuousSliceCycles} ratio={Z80PerM68kRatio:F4} Z80CyclesPerLine={Z80CyclesPerLine} masterSystemMode={g_masterSystemMode}");
            }
            else if (!z80RunPerLine && !_z80FrameScheduleLogged && MdLog.TraceZ80Win)
            {
                _z80FrameScheduleLogged = true;
                Console.WriteLine($"[Z80SCHED] mode=frame budget={z80FrameBudget} lines={lines}");
            }

            for (int vline = 0; vline < lines; vline++)
            {
                if (g_masterSystemMode)
                {
                    g_md_z80?.ResetLineCycles();
                    if (allowZ80 && z80RunPerLine)
                    {
                        g_md_z80.BeginSystemCycleSlice();
                        g_md_z80.run(z80LineBudget);
                        int systemCycles = ConvertZ80ToM68kCycles(z80LineBudget);
                        if (systemCycles < 1) systemCycles = 1;
                        DebugLogSystemCycles("RunFrame-mastersys", z80LineBudget, systemCycles);
                        AdvanceSystemCycles(systemCycles);
                        g_md_z80.EndSystemCycleSlice();
                    }
                    g_md_vdp.run(vline);
                    continue;
                }

                g_md_vdp.run(vline);

                 // Continuous Z80 scheduling proportional to M68K cycles
                // This runs Z80 in small slices throughout each M68K execution slice,
                // maintaining proper timing for YM busy-polling, mailbox communication, etc.
                if (!g_masterSystemMode && UseZ80ContinuousScheduling && allowZ80)
                {
                    int sliceM68kCycles = Z80ContinuousSliceCycles;
                    int totalM68kCycles = VDL_LINE_RENDER_MC68_CLOCK;
                    double z80Budget = _z80ContinuousBudgetCarry;

                    int m68kDone = 0;
                    while (m68kDone < totalM68kCycles)
                    {
                        int m68kSlice = Math.Min(sliceM68kCycles, totalM68kCycles - m68kDone);

                        // Run M68K slice with VDP FIFO/DMA timing
                        frameM68kCalls++;
                        frameM68kBudget += m68kSlice;
                        RunM68kWithVdp(m68kSlice);
                        m68kDone += m68kSlice;

                        // Add Z80 budget proportional to M68K cycles executed
                        z80Budget += m68kSlice * Z80PerM68kRatio;

                        // Run Z80 in small, even slices for better timing
                        // Max 32 cycles per slice to prevent audio jitter
                        while (z80Budget >= 1.0)
                        {
                            g_md_bus?.ApplyZ80BusReqLatch();
                            if (g_md_bus?.Z80BusGranted ?? false)
                            {
                                // Z80 bus is granted to 68k, Z80 cannot run
                                // Budget is consumed by M68K time, not Z80 execution
                                break;
                            }
                            int z80Cycles = Math.Min(32, (int)Math.Floor(z80Budget));
                            if (z80Cycles <= 0) break;

                            g_md_z80.BeginSystemCycleSlice();

                            g_md_z80.run(z80Cycles);
                            z80Budget -= z80Cycles;
                            g_md_z80.EndSystemCycleSlice();
                            
                            // Telemetry: track Z80 execution
                            _z80SliceCount++;
                            _z80TotalCycles += z80Cycles;
                            if (z80Cycles > _z80MaxSlice)
                                _z80MaxSlice = z80Cycles;
                        }
                    }
                    _z80ContinuousBudgetCarry = Math.Clamp(z80Budget, 0.0, 512.0);
                    continue;
                }
                _z80ContinuousBudgetCarry = 0.0;

                // Kör CPU:erna scanline-vis
                if (!g_masterSystemMode && z80RunPerLine && Z80InterleaveSlices > 1)
                {
                    int slices = Z80InterleaveSlices;
                    int m68kSlice = VDL_LINE_RENDER_MC68_CLOCK / slices;
                    int m68kRemainder = VDL_LINE_RENDER_MC68_CLOCK - (m68kSlice * slices);
                    int z80Slice = z80LineBudget / slices;
                    int z80Remainder = z80LineBudget - (z80Slice * slices);
                    for (int s = 0; s < slices; s++)
                    {
                        int m68kCycles = m68kSlice + (s == slices - 1 ? m68kRemainder : 0);
                        int z80Cycles = z80Slice + (s == slices - 1 ? z80Remainder : 0);
                         if (Z80RunBeforeM68k)
                        {
                            if (allowZ80 && z80Cycles > 0)
                            {
                                g_md_z80.BeginSystemCycleSlice();
                                g_md_z80.run(z80Cycles);
                                g_md_z80.EndSystemCycleSlice();
                            }
                            if (m68kCycles > 0)
                            {
                                frameM68kCalls++;
                                frameM68kBudget += m68kCycles;
                                RunM68kWithVdp(m68kCycles);
                            }
                        }
                         else
                        {
                            if (m68kCycles > 0)
                            {
                                frameM68kCalls++;
                                frameM68kBudget += m68kCycles;
                                RunM68kWithVdp(m68kCycles);
                            }
                            if (allowZ80 && z80Cycles > 0)
                            {
                                g_md_z80.BeginSystemCycleSlice();
                                g_md_z80.run(z80Cycles);
                                g_md_z80.EndSystemCycleSlice();
                            }
                        }
                    }
                    continue;
                }

                if (!g_masterSystemMode)
                {
                    if (allowZ80 && z80RunPerLine && Z80RunBeforeM68k)
                    {
                        g_md_z80.BeginSystemCycleSlice();
                        g_md_z80.run(z80LineBudget);
                        g_md_z80.EndSystemCycleSlice();
                    }
                    frameM68kCalls++;
                    frameM68kBudget += VDL_LINE_RENDER_MC68_CLOCK;
                    RunM68kWithVdp(VDL_LINE_RENDER_MC68_CLOCK);
                    if (allowZ80 && z80RunPerLine && !Z80RunBeforeM68k)
                    {
                        g_md_z80.BeginSystemCycleSlice();
                        g_md_z80.run(z80LineBudget);
                        g_md_z80.EndSystemCycleSlice();
                    }
                }
            }
            // In continuous scheduling mode Z80 has already been budgeted during each
            // line slice. Do not run an additional frame-budget pass.
            if (allowZ80 && !z80RunPerLine && !UseZ80ContinuousScheduling)
            {
                g_md_z80.BeginSystemCycleSlice();
                g_md_z80.run(z80FrameBudget);
                if (g_masterSystemMode)
                {
                    // Convert Z80 cycles to M68K cycles for SystemCycles
                    int systemCycles = ConvertZ80ToM68kCycles(z80FrameBudget);
                    if (systemCycles < 1) systemCycles = 1;
                    DebugLogSystemCycles("RunFrame-framebudget", z80FrameBudget, systemCycles);
                    AdvanceSystemCycles(systemCycles);
                }
                g_md_z80.EndSystemCycleSlice();
            }

            // Advance YM2612 timers from SystemCycles once per frame
            g_md_music?.YmEnsureAdvanceEachFrame();

            MaybeInjectMbx(frame);
            g_md_music?.FlushDacRateFrame(frame);
            g_md_music?.FlushAudioStats(frame);
            g_md_bus?.FlushZ80WinHist(frame);
            g_md_bus?.FlushZ80WinStat(frame);
            g_md_bus?.FlushMbx68kStat(frame);
            g_md_bus?.FlushSram(frame.ToString());
            if (TraceSonic2Ram)
                LogSonic2RamChanges(frame);
            if (g_masterSystemMode)
                g_md_z80?.FlushSmsSram(frame.ToString());
            g_md_z80?.FlushZ80MbxPoll(frame);
             g_md_z80?.FlushZ80WaitLoopHist(frame);
            g_md_z80?.FlushPcHist(frame);
            
            // Log Z80 telemetry (gated)
            if (ReadEnvFlag("EUTHERDRIVE_TRACE_TIMING_DEBUG"))
            {
                Console.WriteLine($"[TIMING-DEBUG] frame={frame}: Z80={_z80TotalCycles} m68k={_m68kCyclesThisFrame} timerCtrl={TimerControlCalls} z80Active={g_md_z80?.g_active ?? false}");

                // Per-frame diagnostic logging
                if (ReadEnvFlag("EUTHERDRIVE_DIAG_FRAME"))
                {
                    long currentFrame = g_md_vdp?.FrameCounter ?? -1;
                    if (currentFrame != _lastDiagnosticFrame)
                    {
                        _lastDiagnosticFrame = currentFrame;
                        _z80CyclesLastFrame = (int)_z80TotalCycles;
                        Console.WriteLine($"[DIAG-FRAME] frame={currentFrame} z80Cycles={_z80CyclesLastFrame} m68kCycles={_m68kCyclesThisFrame} ymAdvanceCalls={_ymAdvanceCallsLastFrame}");
                        _ymAdvanceCallsLastFrame = 0;
                    }
                }
            }
            _z80SliceCount = 0;
            _z80TotalCycles = 0;
            _z80MaxSlice = 0;
            _m68kCyclesThisFrame = 0;
            _timerControlCalls = 0;
            g_md_z80?.FlushZ80IntStats(frame); // [INT-STATS] ZINT per-frame stats

            if (g_md_z80 != null)
            {
                var (actual, budget) = g_md_z80.ConsumeFrameCycleStats();
                bool traceCycles = TraceZ80FrameCycles || ReadEnvFlag("EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES");
                if (traceCycles && (TraceZ80FrameCyclesEvery <= 0 || frame % TraceZ80FrameCyclesEvery == 0))
                {
                    Console.WriteLine($"[Z80-CYCLES] frame={frame} actual={actual} budget={budget}");
                }
                _smsCycleLogAccumActual += actual;
                _smsCycleLogAccumBudget += budget;
                long now = _smsCycleLogTimer.ElapsedMilliseconds;
                if (now - _smsCycleLogLastMs >= SmsCycleLogIntervalMs)
                {
                    _smsCycleLogLastMs = now;
                    ushort pc = g_md_z80.DebugPc;
                    Console.WriteLine($"[Z80 CYCLES/sec] budget={_smsCycleLogAccumBudget} actual={_smsCycleLogAccumActual} pc=0x{pc:X4}");
                    _smsCycleLogAccumBudget = 0;
                    _smsCycleLogAccumActual = 0;
                }
                g_md_z80.FlushZ80AudioRate(frame);
            }
            _runFrameCompleteCount++;
            _runFrameLastFrame = frame;
            _runFrameLastLines = lines;
            _runFrameLastM68kCalls = frameM68kCalls;
            _runFrameLastM68kBudget = frameM68kBudget;
        }

        internal static void MaybeInjectMbx(long frame)
        {
            bool injectMbx = IsInjectMbxEnabled(out string? injectEnvRaw);
            if (!_mbxInjectEnvLogged)
            {
                string shown = injectEnvRaw ?? "<null>";
                Console.WriteLine($"[MBXINJ-ENV] value='{shown}'");
                _mbxInjectEnvLogged = true;
            }
            if (injectMbx && !_mbxInjectConfigLoaded)
            {
                _injectMbxAddr = ParseInjectMbxAddr();
                _injectMbxValue = ParseInjectMbxValue();
                _injectMbxFrame = ParseInjectMbxFrame();
                _mbxInjectConfigLoaded = true;
            }
            if (injectMbx && !_mbxInjectArmedLogged)
            {
                Console.WriteLine($"[MBXINJ-ARM] frame={frame} target={_injectMbxFrame} addr=0x{_injectMbxAddr:X4} val=0x{_injectMbxValue:X2}");
                _mbxInjectArmedLogged = true;
            }
            if (injectMbx && !_mbxInjected && frame >= _injectMbxFrame && g_md_bus != null)
            {
                uint addr = 0xA00000u + _injectMbxAddr;
                g_md_bus.write8(addr, _injectMbxValue);
                Console.WriteLine($"[MBXINJ] frame={frame} addr=0x{_injectMbxAddr:X4} val=0x{_injectMbxValue:X2}");
                _mbxInjected = true;
                _mbxInjectAcked = false;
                _mbxInjectPendingClear = true;
                _mbxInjectCleared = false;
            }
            if (injectMbx && _mbxInjected && _mbxInjectPendingClear && _mbxInjectAcked && !_mbxInjectCleared && g_md_bus != null)
            {
                uint addr = 0xA00000u + _injectMbxAddr;
                g_md_bus.write8(addr, 0x00);
                Console.WriteLine($"[MBXINJ-CLR] frame={frame} addr=0x{_injectMbxAddr:X4} val=0x00");
                _mbxInjectCleared = true;
                _mbxInjectPendingClear = false;
            }
        }

        internal static void NotifyMbxInjectedRead(ushort addr, byte value)
        {
            if (!_mbxInjected || !_mbxInjectPendingClear || _mbxInjectAcked)
                return;
            if (addr != _injectMbxAddr || value == 0x00)
                return;
            _mbxInjectAcked = true;
            Console.WriteLine($"[MBXINJ-ACK] addr=0x{addr:X4} val=0x{value:X2}");
        }

        private static ushort ParseInjectMbxAddr()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_INJECT_MBX_ADDR");
            if (string.IsNullOrWhiteSpace(raw))
                return 0x1B8F;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (ushort.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
                return value;
            if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return 0x1B8F;
        }

        private static byte ParseInjectMbxValue()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_INJECT_MBX_VAL");
            if (string.IsNullOrWhiteSpace(raw))
                return 0x83;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (byte.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                return value;
            if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return 0x01;
        }

        private static long ParseInjectMbxFrame()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_INJECT_MBX_FRAME");
            if (string.IsNullOrWhiteSpace(raw))
                return 10;
            if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                return value;
            return 10;
        }

        private static bool IsInjectMbxEnabled(out string? raw)
        {
            raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_INJECT_MBX");
            if (raw == null)
                return false;
            return string.Equals(raw.Trim(), "1", StringComparison.Ordinal);
        }

        private static double ParseSmsCycleMultiplier()
        {
            const double fallback = 1.0;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_CYCLES_MULT");
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed) &&
                parsed > 0.0)
            {
                return parsed;
            }

            return fallback;
        }

        private static double ParseZ80CycleMultiplier()
        {
            const double fallback = 1.0; // Z80 runs at full 3.58MHz, bus contention handled via wait states
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_CYCLES_MULT");
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed) &&
                parsed > 0.0)
            {
                return parsed;
            }

            return fallback;
        }

        public static bool ReadEnvFlag(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            raw = raw.Trim();
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseNonNegativeInt(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0)
                return value;
            return fallback;
        }

        private static uint ParseForceSeegaPtr()
        {
            const uint fallback = 0x00002020;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_SEEGA_PTR");
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                return value;
            if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        private static int ParseZ80RunPerLineFrames()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_RUN_PER_LINE_FRAMES");
            if (string.IsNullOrWhiteSpace(raw))
                return 0;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            return 0;
        }

        private static void LogSonic2RamChanges(long frame)
        {
            if (_traceSonic2RamRemaining <= 0)
                return;

            const uint baseAddr = 0xFFB000;
            if (!_sonic2RamInit)
            {
                for (int i = 0; i < _sonic2RamLast.Length; i++)
                    _sonic2RamLast[i] = md_m68k.read8(baseAddr + (uint)i);
                _sonic2RamInit = true;
                return;
            }

            for (int i = 0; i < _sonic2RamLast.Length; i++)
            {
                byte cur = md_m68k.read8(baseAddr + (uint)i);
                if (cur == _sonic2RamLast[i])
                    continue;

                _sonic2RamLast[i] = cur;
                Console.WriteLine($"[S2-RAM] frame={frame} addr=0x{baseAddr + (uint)i:X6} val=0x{cur:X2}");
                if (_traceSonic2RamRemaining != int.MaxValue)
                {
                    _traceSonic2RamRemaining--;
                    if (_traceSonic2RamRemaining <= 0)
                        return;
                }
            }
        }

        private static int ParseZ80InterleaveSlices()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_INTERLEAVE_SLICES");
            if (string.IsNullOrWhiteSpace(raw))
                return 1;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            return 1;
        }

        private static int ParseZ80WaitBusReqFrames()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_WAIT_BUSREQ_FRAMES");
            if (string.IsNullOrWhiteSpace(raw))
                return 0;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            return 0;
        }

        private static int ParseZ80WaitBusReqMaxFrames()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_WAIT_BUSREQ_MAX_FRAMES");
            if (string.IsNullOrWhiteSpace(raw))
                return 0;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            return 0;
        }

        private static int ParseZ80ContinuousSliceCycles()
        {
            // When > 0, enables continuous Z80 scheduling proportional to M68K cycles
            // Value = how many M68K cycles between Z80 slices (smaller = more frequent)
            // Default 8 = more frequent arbitration windows for BUSREQ-heavy drivers
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_CONTINUOUS_SLICE_M68K_CYCLES");
            if (string.IsNullOrWhiteSpace(raw))
                return 8; // Default enabled with tighter interleave
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            return 8; // Default enabled
        }

        internal static void ResetZ80WaitState()
        {
            _z80WaitFrames = 0;
            _z80StableLowFrames = 0;
            _z80WaitReleased = Z80WaitBusReqFrames <= 0;
            _z80WaitLogged = false;
            _z80ContinuousBudgetCarry = 0.0;
        }

        internal static bool ShouldRunZ80(long frame)
        {
            // DEBUG - always log first 10 frames (gated)
            if (TraceShouldRun && frame <= 10)
            {
                bool debugBusReq = g_md_bus?.Z80BusGranted ?? false;
                bool debugReset = g_md_bus?.Z80Reset ?? false;
                Console.WriteLine($"[SHOULDRUN] frame={frame} masterSystemMode={g_masterSystemMode} Z80WaitBusReqFrames={Z80WaitBusReqFrames} _z80WaitReleased={_z80WaitReleased} busReq={debugBusReq} reset={debugReset}");
            }
            
            if (g_masterSystemMode)
                return true;
            if (Z80WaitBusReqFrames <= 0)
                return true;
            if (_z80WaitReleased)
                return true;
            
            // DEBUG: Log Sonic 2 Z80 scheduling (gated)
            if (TraceShouldRun && frame >= 4899 && frame <= 4905)
            {
                bool debugBusReq = g_md_bus?.Z80BusGranted ?? false;
                bool debugZ80Reset = g_md_bus?.Z80Reset ?? false;
                Console.WriteLine($"[SONIC2-SHOULDRUN] frame={frame} busReq={debugBusReq} z80reset={debugZ80Reset} _z80StableLowFrames={_z80StableLowFrames} _z80WaitFrames={_z80WaitFrames} _z80WaitReleased={_z80WaitReleased}");
            }

            bool busReq = g_md_bus?.Z80BusGranted ?? false;
            bool reset = g_md_bus?.Z80Reset ?? false;
            if (!busReq && !reset)
                _z80StableLowFrames++;
            else
                _z80StableLowFrames = 0;
            _z80WaitFrames++;

            bool release = _z80StableLowFrames >= Z80WaitBusReqFrames;
            if (!release && Z80WaitBusReqMaxFrames > 0 && _z80WaitFrames >= Z80WaitBusReqMaxFrames)
                release = true;

            if (release)
            {
                _z80WaitReleased = true;
                if (Z80WaitBusReqLog && !_z80WaitLogged)
                {
                    _z80WaitLogged = true;
                    Console.WriteLine(
                        $"[Z80WAIT] release frame={frame} stable={_z80StableLowFrames} waited={_z80WaitFrames} " +
                        $"busReq={(busReq ? 1 : 0)} reset={(reset ? 1 : 0)}");
                }
            }

            return _z80WaitReleased;
        }

        internal static void SafeResetZ80()
        {
            try
            {
                BeginZ80ResetCycle();
                g_md_z80?.reset();
            }
            catch (System.Exception ex)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MAIN"), "1", StringComparison.Ordinal))
                    Console.WriteLine($"[md_main] SafeResetZ80 failed: {ex.Message}");
            }
        }
    }
}
