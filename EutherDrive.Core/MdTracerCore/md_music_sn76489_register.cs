namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_sn76489
    {
        private static readonly bool TraceAudStat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDSTAT"), "1", StringComparison.Ordinal);
        private int _audStatWrites;

        public void write8(byte in_val)
        {
            if (TraceAudStat)
                _audStatWrites++;

            // DEBUG: Log all PSG writes
            string type = (in_val & 0x80) != 0 ? "latch" : "data";
            Console.WriteLine($"[PSG-WRITE] val=0x{in_val:X2} ({type})");
            
            if ((in_val & 0x80) != 0)
            {
                int w_num = (in_val >> 5) & 0x03;

                if ((in_val & 0x10) == 0)
                {
                    // Tone/noise latch
                    if (w_num <= 2)
                    {
                        // lower 4 bits av 10-bitars frekvens
                        g_freq[w_num] = (g_freq[w_num] & 0x03F0) | (in_val & 0x0F);
                        if (g_freq[w_num] < FREQ_MIN) g_freq[w_num] = FREQ_MIN;
                        g_write_num_bk = w_num;
                    }
                    else
                    {
                        // Noise
                        g_shift_reg = NOISEINITIAL;
                        g_freq[3] = 0x10 << (in_val & 0x03);   // 0x10, 0x20, 0x40, eller 0x80 (ch2 clock)
                        g_noise_mode = (in_val & 0x04) != 0;
                        g_write_num_bk = -1;
                    }
                }
                else
                {
                    // Volume (4 bit)
                    g_vol[w_num] = VOL_MAP[in_val & 0x0F];
                    g_write_num_bk = -1;
                }

                // Exponera frekvens till UI/logg om mastervolymen inte mutar kanalen
                if (w_num <= 2)
                {
                    int idx = 6 + w_num; // PSG visas på index 6..8 i md_music
                    if (idx >= 0 && idx < md_main.g_md_music.g_freq_out.Length)
                    {
                        if (g_vol[w_num] == 0)
                            md_main.g_md_music.g_freq_out[idx] = 0;
                        else
                            md_main.g_md_music.g_freq_out[idx] = (int)(PSG_CLOCK / ((g_freq[w_num] + 1) << 4));
                    }
                }
            }
            else
            {
                // Andra halvan av 10-bitars frekvens (6 bitar)
                if (g_write_num_bk != -1)
                {
                    int ch = g_write_num_bk;
                    g_freq[ch] = (g_freq[ch] & 0x000F) | ((in_val & 0x3F) << 4);
                    if (g_freq[ch] < FREQ_MIN) g_freq[ch] = FREQ_MIN;
                    g_write_num_bk = -1;
                }
            }
        }

        internal int ConsumeAudStatWrites()
        {
            if (!TraceAudStat)
                return 0;
            int value = _audStatWrites;
            _audStatWrites = 0;
            return value;
        }

        // Volume table - matches classic SN76489 attenuation values
        private static readonly int[] VOL_MAP = new int[]
        {
            4095, 3267, 2594, 2062, 1638, 1301, 1032, 819,
            651,  518,  411,  326,  259,  206,  164,   0
        };
    }
}
