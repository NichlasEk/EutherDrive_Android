namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_sn76489
    {
        private static readonly bool TraceAudStat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDSTAT"), "1", StringComparison.Ordinal);
        private static readonly bool TracePsgWrite =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PSG_WRITE"), "1", StringComparison.Ordinal);
        private static readonly bool TracePsgVoice =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PSG_VOICE"), "1", StringComparison.Ordinal);
        private const int PsgVoiceHistorySize = 128;
        private static readonly PsgVoiceEvent[] _psgVoiceHistory = new PsgVoiceEvent[PsgVoiceHistorySize];
        private static int _psgVoiceHistoryIndex;
        private static long _psgVoiceLastDumpFrame = -9999;
        private int _audStatWrites;

        public void write8(byte in_val)
        {
            if (TraceAudStat)
                _audStatWrites++;

            // DEBUG: Log all PSG writes
            if (TracePsgWrite)
            {
                string type = (in_val & 0x80) != 0 ? "latch" : "data";
                Console.WriteLine($"[PSG-WRITE] val=0x{in_val:X2} ({type})");
            }
            if (TracePsgVoice)
                RecordPsgVoiceEvent(in_val);
            
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

        private static void RecordPsgVoiceEvent(byte value)
        {
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            _psgVoiceHistory[_psgVoiceHistoryIndex] = new PsgVoiceEvent(frame, value);
            _psgVoiceHistoryIndex = (_psgVoiceHistoryIndex + 1) % PsgVoiceHistorySize;

            // Heuristic: dump when we see a loud volume latch (volume <= 2).
            if ((value & 0x80) == 0x80 && (value & 0x10) == 0x10)
            {
                int vol = value & 0x0F;
                if (vol <= 2 && frame - _psgVoiceLastDumpFrame >= 30)
                {
                    _psgVoiceLastDumpFrame = frame;
                    DumpPsgVoiceHistory(frame);
                }
            }
        }

        private static void DumpPsgVoiceHistory(long frame)
        {
            Console.WriteLine($"[PSG-VOICE] frame={frame} last={PsgVoiceHistorySize} writes (latest last):");
            int start = _psgVoiceHistoryIndex;
            for (int i = 0; i < PsgVoiceHistorySize; i++)
            {
                int idx = (start + i) % PsgVoiceHistorySize;
                var ev = _psgVoiceHistory[idx];
                if (ev.Frame < 0)
                    continue;
                string type = (ev.Value & 0x80) != 0 ? "L" : "D";
                int chan = (ev.Value >> 5) & 0x03;
                int vol = ev.Value & 0x0F;
                Console.WriteLine($"[PSG-VOICE] f={ev.Frame} {type} ch={chan} val=0x{ev.Value:X2} vol={vol}");
            }
        }

        private readonly struct PsgVoiceEvent
        {
            public readonly long Frame;
            public readonly byte Value;
            public PsgVoiceEvent(long frame, byte value)
            {
                Frame = frame;
                Value = value;
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
