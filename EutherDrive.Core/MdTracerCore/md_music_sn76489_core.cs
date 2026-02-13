using System;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    //PSG  : chips: Texas Instruments SN76489
    //----------------------------------------------------------------
    internal partial class md_sn76489
    {
        private static readonly bool AudioMuteFmPsg =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_MUTE_FMPSG"), "1", StringComparison.Ordinal);
        //const
        private const int PSG_CLOCK = 3579545;
        private const int PSG_SAMPLING = 44100;
        private const float CLOCK_INC = (float)PSG_CLOCK / PSG_SAMPLING / 16;
        private const int PSG_GAIN_NUM = 3;
        private const int PSG_GAIN_DEN = 4;
        private const int CHANNEL_NUM = 4;
        private const int NOISE_CHANNEL = 3;
        private const int FREQ_MIN = 5;     //レート44100の最大周波数は22050Hzなので周波数の最小は5
        private const int WHITENOISE = 0x0009;
        private const int NOISESHIFT = 14;     // 15-bit LFSR (bits 0..14)
        private const int NOISEINITIAL = 0x4000;

        //work
        private float[] g_psg_clock = Array.Empty<float>();
        private int[] g_channel_out = Array.Empty<int>();
        private int[] g_freq = Array.Empty<int>();
        private int[] g_vol = Array.Empty<int>();
        private bool[] g_duty = Array.Empty<bool>();
        private bool g_noise_mode;
        private int g_write_num_bk;
        private int g_shift_reg;
        private float g_ch2_clock;
        private int _noiseGainPercent = 100;
        private const float PSG_SOFTCLIP_SCALE = 12000f;
        private const float PSG_DC_BLOCK_R = 0.995f;
        private float _dcBlockX1;
        private float _dcBlockY1;

        public void SetNoiseGainPercent(int percent)
        {
            if (percent < 0) percent = 0;
            else if (percent > 200) percent = 200;
            _noiseGainPercent = percent;
        }

        private static float SoftClip(float x)
        {
            return x;
        }

        public void SN76489_Start()
        {
            g_psg_clock = new float[CHANNEL_NUM];
            g_channel_out = new int[CHANNEL_NUM];
            g_freq = new int[CHANNEL_NUM];
            g_vol = new int[CHANNEL_NUM];
            g_duty = new bool[4];
            g_write_num_bk = -1;
            g_shift_reg = NOISEINITIAL;

            for (int w_ch = 0; w_ch <= 3; w_ch++)
            {
                g_freq[w_ch] = 1;
                g_vol[w_ch] = VOL_MAP[15];
                g_freq[3] = 0x10;
            }
        }

        public int SN76489_Update()
        {
            int w_out = 0;
            //toon
            bool tone2Ticked = false;
            for (int w_ch = 0; w_ch <= 2; w_ch++)
            {
                g_channel_out[w_ch] = (g_duty[w_ch] == true) ? g_vol[w_ch] : -g_vol[w_ch];
                g_psg_clock[w_ch] -= CLOCK_INC;
                if (w_ch == 2) g_ch2_clock = g_psg_clock[2];
                if (g_psg_clock[w_ch] <= 0)
                {
                    if (g_freq[w_ch] >= FREQ_MIN)
                    {
                        g_duty[w_ch] = !g_duty[w_ch];
                        if (w_ch == 2)
                            tone2Ticked = true;
                    }
                    g_psg_clock[w_ch] += g_freq[w_ch];
                }
                
                // DEBUG: Log PSG state
                // if (w_ch == 0 && g_vol[0] > 0)
                // {
                //     Console.WriteLine($"[PSG-DEBUG] ch={w_ch} vol={g_vol[w_ch]} freq={g_freq[w_ch]} duty={g_duty[w_ch]} out={g_channel_out[w_ch]}");
                // }
            }

            //noise
            {
                int noiseBit = g_shift_reg & 0x1;
                g_channel_out[NOISE_CHANNEL] = noiseBit == 0 ? g_vol[NOISE_CHANNEL] : -g_vol[NOISE_CHANNEL];
                if (g_freq[3] == 0x80)
                {
                    // Noise clocked by tone channel 2
                    if (tone2Ticked)
                    {
                        int w_bit1;
                        if (g_noise_mode == true)
                        {
                            // White noise (Sega variant): XOR bit0 and bit3, insert into bit14
                            w_bit1 = ((g_shift_reg & 1) ^ ((g_shift_reg >> 3) & 1)) & 1;
                        }
                        else
                        {
                            // Periodic noise: use LSB
                            w_bit1 = g_shift_reg & 1;
                        }
                        g_shift_reg = (g_shift_reg >> 1) | (w_bit1 << NOISESHIFT);
                    }
                }
                else
                {
                    g_psg_clock[NOISE_CHANNEL] -= CLOCK_INC;
                    if (g_psg_clock[NOISE_CHANNEL] <= 0)
                    {
                        g_psg_clock[NOISE_CHANNEL] += g_freq[3];

                        int w_bit1;
                        if (g_noise_mode == true)
                        {
                            // White noise (Sega variant): XOR bit0 and bit3, insert into bit14
                            w_bit1 = ((g_shift_reg & 1) ^ ((g_shift_reg >> 3) & 1)) & 1;
                        }
                        else
                        {
                            // Periodic noise: use LSB
                            w_bit1 = g_shift_reg & 1;
                        }
                        g_shift_reg = (g_shift_reg >> 1) | (w_bit1 << NOISESHIFT);
                    }
                }
            }
            //mix
            if (AudioMuteFmPsg)
                return 0;
            float mixedSum = 0;
            for (int w_ch = 0; w_ch <= 3; w_ch++)
            {
                float sample = g_channel_out[w_ch];
                if (w_ch == NOISE_CHANNEL && _noiseGainPercent != 100)
                    sample = sample * _noiseGainPercent / 100f;
                sample *= md_main.g_md_music.g_out_vol[w_ch + 6];
                mixedSum += sample;
            }
            mixedSum *= (float)PSG_GAIN_NUM / PSG_GAIN_DEN;
            float dcBlocked = mixedSum - _dcBlockX1 + (PSG_DC_BLOCK_R * _dcBlockY1);
            _dcBlockX1 = mixedSum;
            _dcBlockY1 = dcBlocked;
            float clipped = SoftClip(dcBlocked / PSG_SOFTCLIP_SCALE) * PSG_SOFTCLIP_SCALE;
            if (clipped > short.MaxValue) clipped = short.MaxValue;
            else if (clipped < short.MinValue) clipped = short.MinValue;
            return (int)MathF.Round(clipped);
        }
    }
}
