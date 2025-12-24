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
        private static int SmsCyclesPerLine => Math.Max(1, (int)(VDL_LINE_RENDER_Z80_CLOCK * SmsCycleMultiplier));
        private static readonly Stopwatch _smsCycleLogTimer = Stopwatch.StartNew();
        private static long _smsCycleLogLastMs;
        private static long _smsCycleLogAccumBudget;
        private static long _smsCycleLogAccumActual;
        private const int SmsCycleLogIntervalMs = 1000;

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
                g_hard_reset_req = false;
            }

            int lines = g_md_vdp.g_vertical_line_max;
            int z80LineBudget = SmsCyclesPerLine;

            for (int vline = 0; vline < lines; vline++)
            {
                g_md_vdp.run(vline);

                // Kör CPU:erna scanline-vis
                if (!g_masterSystemMode)
                    g_md_m68k.run(VDL_LINE_RENDER_MC68_CLOCK);
                g_md_z80.run(z80LineBudget);
            }

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
