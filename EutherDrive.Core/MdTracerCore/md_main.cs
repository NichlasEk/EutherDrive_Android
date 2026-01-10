using System;
using System.Diagnostics;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    internal static partial class md_main
    {
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
        private static readonly double Z80CycleMultiplier = ParseZ80CycleMultiplier();
        private static readonly bool ForceZ80RunPerLine = ReadEnvFlag("EUTHERDRIVE_Z80_RUN_PER_LINE");
        private static readonly int Z80RunPerLineFrames = ParseZ80RunPerLineFrames();
        private static readonly bool Z80RunBeforeM68k = ReadEnvFlag("EUTHERDRIVE_Z80_RUN_BEFORE_M68K");
        private static readonly int Z80InterleaveSlices = ParseZ80InterleaveSlices();
        private static readonly int Z80WaitBusReqFrames = ParseZ80WaitBusReqFrames();
        private static readonly int Z80WaitBusReqMaxFrames = ParseZ80WaitBusReqMaxFrames();
        private static readonly bool Z80WaitBusReqLog = ReadEnvFlag("EUTHERDRIVE_Z80_WAIT_BUSREQ_LOG");
        // Z80/M68K cycle ratio for NTSC: Z80 ~3.58MHz, M68K ~7.67MHz => ratio ~0.466
        private static readonly double Z80PerM68kRatio = 3.58 / 7.67;
        private static readonly int Z80ContinuousSliceCycles = ParseZ80ContinuousSliceCycles();
        private static bool UseZ80ContinuousScheduling => !g_masterSystemMode && Z80ContinuousSliceCycles > 0;
        private static int SmsCyclesPerLine => Math.Max(1, (int)(VDL_LINE_RENDER_Z80_CLOCK * SmsCycleMultiplier));
        private static int Z80CyclesPerLine => Math.Max(1, (int)(VDL_LINE_RENDER_Z80_CLOCK * Z80CycleMultiplier));
        private static readonly Stopwatch _smsCycleLogTimer = Stopwatch.StartNew();
        private static long _smsCycleLogLastMs;
        private static long _smsCycleLogAccumBudget;
        private static long _smsCycleLogAccumActual;
        private const int SmsCycleLogIntervalMs = 1000;
        private static bool _z80FrameScheduleLogged;
        private static int _z80WaitFrames;
        private static int _z80StableLowFrames;
        private static bool _z80WaitReleased;
        private static bool _z80WaitLogged;
        private static bool _mbxInjected;
        private static bool _mbxInjectAcked;
        private static bool _mbxInjectPendingClear;
        private static bool _mbxInjectCleared;
        private static bool _mbxInjectArmedLogged;
        private static bool _mbxInjectEnvLogged;
        private static bool _mbxInjectConfigLoaded;
        private static ushort _injectMbxAddr;
        private static byte _injectMbxValue;
        private static long _injectMbxFrame;

        // --- System-wide monotonic cycle counter for YM2612 timing ---
        // This counter advances as the system executes, providing a monotonic
        // timebase for YM2612 busy checks that doesn't depend on per-frame budgets.
        // We use M68K cycles as the base unit (VDL_LINE_RENDER_MC68_CLOCK per line).
        private static long _systemCycles;
        internal static long SystemCycles => _systemCycles;
        internal static void AdvanceSystemCycles(long cycles) => _systemCycles += cycles;

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

            if (g_hard_reset_req)
            {
                g_md_m68k.reset();
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
            int z80LineBudget = g_masterSystemMode ? SmsCyclesPerLine : Z80CyclesPerLine;
            bool z80RunPerLine = g_masterSystemMode;
            if (!g_masterSystemMode && ForceZ80RunPerLine)
            {
                z80RunPerLine = Z80RunPerLineFrames <= 0 || (frame >= 0 && frame < Z80RunPerLineFrames);
            }
            bool allowZ80 = ShouldRunZ80(frame);
            int z80FrameBudget = z80LineBudget * lines;
            if (UseZ80ContinuousScheduling && !_z80FrameScheduleLogged)
            {
                _z80FrameScheduleLogged = true;
                Console.WriteLine($"[Z80SCHED] mode=continuous sliceM68k={Z80ContinuousSliceCycles} ratio={Z80PerM68kRatio:F4}");
            }
            else if (!z80RunPerLine && !_z80FrameScheduleLogged && MdLog.TraceZ80Win)
            {
                _z80FrameScheduleLogged = true;
                Console.WriteLine($"[Z80SCHED] mode=frame budget={z80FrameBudget} lines={lines}");
            }

            for (int vline = 0; vline < lines; vline++)
            {
                g_md_vdp.run(vline);

                // Continuous Z80 scheduling proportional to M68K cycles
                // This runs Z80 in small slices throughout each M68K execution slice,
                // maintaining proper timing for YM busy-polling, mailbox communication, etc.
                if (!g_masterSystemMode && UseZ80ContinuousScheduling && allowZ80)
                {
                    int sliceM68kCycles = Z80ContinuousSliceCycles;
                    int totalM68kCycles = VDL_LINE_RENDER_MC68_CLOCK;
                    double z80Budget = 0.0;

                    int m68kDone = 0;
                    while (m68kDone < totalM68kCycles)
                    {
                        int m68kSlice = Math.Min(sliceM68kCycles, totalM68kCycles - m68kDone);

                        // Run M68K slice
                        g_md_m68k.run(m68kSlice);
                        AdvanceSystemCycles(m68kSlice);
                        m68kDone += m68kSlice;

                        // Add Z80 budget proportional to M68K cycles executed
                        z80Budget += m68kSlice * Z80PerM68kRatio;

                        // Run Z80 while we have budget
                        while (z80Budget >= 1.0)
                        {
                            if (!(g_md_bus?.Z80BusGranted ?? true))
                            {
                                // Z80 bus is not granted, it cannot run
                                // Budget is consumed by M68K time, not Z80 execution
                                break;
                            }
                            int z80Cycles = (int)Math.Floor(z80Budget);
                            g_md_z80.run(z80Cycles);
                            z80Budget -= z80Cycles;
                        }
                    }
                    continue;
                }

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
                                g_md_z80.run(z80Cycles);
                            if (m68kCycles > 0)
                            {
                                g_md_m68k.run(m68kCycles);
                                AdvanceSystemCycles(m68kCycles);
                            }
                        }
                        else
                        {
                            if (m68kCycles > 0)
                            {
                                g_md_m68k.run(m68kCycles);
                                AdvanceSystemCycles(m68kCycles);
                            }
                            if (allowZ80 && z80Cycles > 0)
                                g_md_z80.run(z80Cycles);
                        }
                    }
                    continue;
                }

                if (!g_masterSystemMode)
                {
                    if (allowZ80 && z80RunPerLine && Z80RunBeforeM68k)
                        g_md_z80.run(z80LineBudget);
                    g_md_m68k.run(VDL_LINE_RENDER_MC68_CLOCK);
                    AdvanceSystemCycles(VDL_LINE_RENDER_MC68_CLOCK);
                    if (allowZ80 && z80RunPerLine && !Z80RunBeforeM68k)
                        g_md_z80.run(z80LineBudget);
                }
                else if (allowZ80 && z80RunPerLine)
                {
                    g_md_z80.run(z80LineBudget);
                }
            }
            if (allowZ80 && !z80RunPerLine)
                g_md_z80.run(z80FrameBudget);

            MaybeInjectMbx(frame);
            g_md_music?.g_md_ym2612.FlushDacRateFrame(frame);
            g_md_music?.FlushAudioStats(frame);
            g_md_bus?.FlushZ80WinHist(frame);
            g_md_bus?.FlushZ80WinStat(frame);
            g_md_bus?.FlushMbx68kStat(frame);
            g_md_bus?.FlushSram(frame.ToString());
            g_md_z80?.FlushZ80MbxPoll(frame);
            g_md_z80?.FlushZ80WaitLoopHist(frame);
            g_md_z80?.FlushPcHist(frame);
            g_md_z80?.FlushZ80IntStats(frame); // [INT-STATS] ZINT per-frame stats

            if (g_md_z80 != null)
            {
                var (actual, budget) = g_md_z80.ConsumeFrameCycleStats();
                _smsCycleLogAccumActual += actual;
                _smsCycleLogAccumBudget += budget;
                long now = _smsCycleLogTimer.ElapsedMilliseconds;
                if (now - _smsCycleLogLastMs >= SmsCycleLogIntervalMs)
                {
                    _smsCycleLogLastMs = now;
                    ushort pc = g_md_z80.DebugPc;
                    Console.WriteLine($"[SMS CYCLES] budget/sec={_smsCycleLogAccumBudget} actual/sec={_smsCycleLogAccumActual} pc=0x{pc:X4}");
                    _smsCycleLogAccumBudget = 0;
                    _smsCycleLogAccumActual = 0;
                }
            }
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
            const double fallback = 1.0;
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

        private static bool ReadEnvFlag(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            raw = raw.Trim();
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
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
            // Default 0 = disabled (use per-frame or per-line scheduling)
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_CONTINUOUS_SLICE_M68K_CYCLES");
            if (string.IsNullOrWhiteSpace(raw))
                return 0;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
                return value;
            return 0;
        }

        internal static void ResetZ80WaitState()
        {
            _z80WaitFrames = 0;
            _z80StableLowFrames = 0;
            _z80WaitReleased = Z80WaitBusReqFrames <= 0;
            _z80WaitLogged = false;
        }

        internal static bool ShouldRunZ80(long frame)
        {
            if (g_masterSystemMode)
                return true;
            if (Z80WaitBusReqFrames <= 0)
                return true;
            if (_z80WaitReleased)
                return true;

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
                Debug.WriteLine($"[md_main] Z80 reset skipped: {ex.Message}");
            }
        }
    }
}
