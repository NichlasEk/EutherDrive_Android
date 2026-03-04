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
                // LATCH/DATA byte: select register and write low bits.
                g_latched_register = DecodeLatchedRegister(in_val);
                WriteRegisterLowBits(g_latched_register, in_val);
            }
            else
            {
                // DATA byte:
                // - For tone registers, this writes the high 6 bits.
                // - For noise/volume registers, this writes low bits again.
                WriteRegisterData(g_latched_register, in_val);
            }
            
            UpdatePsgFreqOut();
        }

        private static int DecodeLatchedRegister(byte value)
        {
            int channel = (value >> 5) & 0x03;
            bool isVolume = (value & 0x10) != 0;
            if (isVolume)
            {
                return 1 + (channel << 1); // vol0,vol1,vol2,vol3 => 1,3,5,7
            }
            if (channel == 3)
            {
                return 6; // noise
            }
            return channel << 1; // tone0,tone1,tone2 => 0,2,4
        }

        private void WriteRegisterLowBits(int reg, byte value)
        {
            switch (reg)
            {
                case 0: // tone0 low
                case 2: // tone1 low
                case 4: // tone2 low
                {
                    int ch = reg >> 1;
                    g_freq[ch] = (g_freq[ch] & 0x03F0) | (value & 0x0F);
                    if (g_freq[ch] < FREQ_MIN) g_freq[ch] = FREQ_MIN;
                    break;
                }
                case 6: // noise
                {
                    g_shift_reg = NOISEINITIAL;
                    g_freq[3] = 0x10 << (value & 0x03); // 0x10,0x20,0x40 or 0x80 (tone2 clock)
                    g_noise_mode = (value & 0x04) != 0;
                    break;
                }
                case 1: // vol0
                case 3: // vol1
                case 5: // vol2
                case 7: // vol3
                {
                    int ch = reg >> 1;
                    g_vol[ch] = VOL_MAP[value & 0x0F];
                    break;
                }
            }
        }

        private void WriteRegisterData(int reg, byte value)
        {
            switch (reg)
            {
                case 0: // tone0 high
                case 2: // tone1 high
                case 4: // tone2 high
                {
                    int ch = reg >> 1;
                    g_freq[ch] = (g_freq[ch] & 0x000F) | ((value & 0x3F) << 4);
                    if (g_freq[ch] < FREQ_MIN) g_freq[ch] = FREQ_MIN;
                    break;
                }
                default:
                    // jgenesis-compatible behavior: DATA byte also updates low bits for
                    // noise/volume registers using the currently latched register.
                    WriteRegisterLowBits(reg, value);
                    break;
            }
        }

        private void UpdatePsgFreqOut()
        {
            for (int ch = 0; ch <= 2; ch++)
            {
                int idx = 6 + ch; // PSG visas på index 6..8 i md_music
                if (idx < 0 || idx >= md_main.g_md_music.g_freq_out.Length)
                    continue;
                if (g_vol[ch] == 0)
                    md_main.g_md_music.g_freq_out[idx] = 0;
                else
                    md_main.g_md_music.g_freq_out[idx] = (int)(PSG_CLOCK / ((g_freq[ch] + 1) << 4));
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
