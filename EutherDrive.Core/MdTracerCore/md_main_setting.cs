using System.Collections.Generic;

namespace EutherDrive.Core.MdTracerCore
{
    internal static partial class md_main
    {
        // Enkla behållare om du vill fylla på senare
        public static List<string> g_setting_name = new();
        public static List<string> g_setting_val  = new();

        // Behåll dessa om andra delar refererar dem
        public static int g_tvmode_req;
        public static int g_gpu_req;

        /// <summary>
        /// Headless: Läs standardinställningar.
        /// (Ingen fil I/O ännu – läggs på senare när UI finns.)
        /// </summary>
        public static void read_setting()
        {
            // I headless-läget: bara defaults.
            read_init();
        }

        /// <summary>
        /// Sätt trygga defaults för input + ljud.
        /// Denna metod måste tåla att g_md_io/g_md_music saknas.
        /// </summary>
        public static void read_init()
        {
            // ---- Input defaults (bara om md_io-instansen finns och är redo) ----
            try
            {
                // g_md_io kan vara null i din portning (eller ej instansbar)
                if (g_md_io != null && g_md_io.g_key_allocation != null && g_md_io.g_key_allocation.Length >= 8)
                {
                    // Tangentmappning (exempel, behåll från original)
                    g_md_io.g_key_allocation[0] = 49;
                    g_md_io.g_key_allocation[1] = 50;
                    g_md_io.g_key_allocation[2] = 51;
                    g_md_io.g_key_allocation[3] = 35;
                    g_md_io.g_key_allocation[4] = 17;
                    g_md_io.g_key_allocation[5] = 31;
                    g_md_io.g_key_allocation[6] = 30;
                    g_md_io.g_key_allocation[7] = 32;
                }
            }
            catch
            {
                // no-op (headless ska inte dö på settings)
            }

            // ---- Audio defaults (bara om md_music-instansen finns och är redo) ----
            try
            {
                if (g_md_music != null)
                {
                    if (g_md_music.g_master_chk != null)
                    {
                        int max = g_md_music.g_master_chk.Length - 1;
                        int end = max < 10 ? max : 10;
                        for (int j = 0; j <= end; j++)
                            g_md_music.g_master_chk[j] = true;
                    }

                    if (g_md_music.g_master_vol != null)
                    {
                        int max = g_md_music.g_master_vol.Length - 1;
                        int end = max < 10 ? max : 10;
                        for (int j = 0; j <= end; j++)
                            g_md_music.g_master_vol[j] = 100;
                    }
                }
            }
            catch
            {
                // no-op
            }

            // ---- TV/GPU defaults ----
            g_tvmode_req = 0;
            g_gpu_req    = 0;

            // OBS: I din nuvarande portning verkar VDP vara statisk (md_vdp.*),
            // så vi skriver inte till "g_md_vdp" här.
            // När/om du senare gör VDP till instans igen kan du återinföra:
            //   g_md_vdp.g_vdp_status_0_tvmode = (byte)g_tvmode_req;
            //   g_md_vdp.rendering_gpu = (g_gpu_req == 1);
        }

        /// <summary>
        /// Headless: no-op. Lägg på riktig persistens senare.
        /// </summary>
        public static void write_setting()
        {
            // TODO: Skriv settings till JSON/TOML när UI finns.
        }

        /// <summary>
        /// Behåll API:et om något råkar kalla det.
        /// I headless: lagra i listorna för debugging/inspektion.
        /// </summary>
        public static void setting_add(string in_name, string in_val)
        {
            // Minimal, men användbart: logga i minnet istället för no-op.
            g_setting_name.Add(in_name);
            g_setting_val.Add(in_val);
        }
    }
}
