using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal static partial class md_main
    {
        /// <summary>
        /// Initiera kärnkomponenterna. Ingen UI, ingen evighetsloop.
        /// Körs en gång tidigt, innan run()/RunFrame().
        /// Idempotent: kan kallas flera gånger utan att skapa dubletter.
        /// </summary>
        public static void initialize()
        {
            // Processprioritet: valfritt; kan kasta på vissa plattformar. Svälj fel.
            try
            {
                using var p = Process.GetCurrentProcess();
                p.PriorityClass = ProcessPriorityClass.High;
            }
            catch
            {
                // no-op
            }

            // Skapa kärnobjekt (bara om de saknas)
            g_md_cartridge ??= new md_cartridge();
            g_md_bus       ??= new md_bus();
            g_md_control   ??= new md_control();
            g_md_m68k      ??= new md_m68k();
            g_md_z80       ??= new md_z80();
            g_md_music     ??= new md_music();
            g_md_vdp       ??= new md_vdp();


            // md_io: bara om md_io är en instansklass. Om md_io är static class: TA BORT g_md_io helt.
            if (g_md_io is null)
            {
                try
                {
                    g_md_io = new md_io();
                    md_io.Current = g_md_io;
                }
                catch
                {
                    // Om md_io inte går att instansiera i din nuvarande portning, ignorera för stunden.
                    // Viktigt: låt inte initialize() dö här.
                    g_md_io = null;
                }
            }

            // Trace-instans (rätt typ, inte bool)
            g_form_code_trace ??= new Form_Code_Trace();

            // Nollställ flaggor (UI kan slå på dem senare)
            g_masterSystemMode = false;
            g_screenA_enable = g_screenB_enable = g_screenW_enable = g_screenS_enable = false;
            g_pattern_enable = g_pallete_enable = g_code_enable = false;
            g_io_enable = g_music_enable = g_registry_enable = g_flow_enable = false;

            g_trace_fsb = false;
            g_trace_sip = false;
            g_hard_reset_req = false;
            g_trace_nextframe = false;

            // CPU-usage meter (om den finns i denna partial)
            g_task_usage = 0;
        }
    }
}
