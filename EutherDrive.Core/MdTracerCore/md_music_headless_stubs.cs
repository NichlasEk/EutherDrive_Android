namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_music
    {
        // YM2612 använder w_ch 0..5 => index 0..5
        // SN76489 i din kod använder w_ch + 6 => index 6..9
        // Så minst 10. Vi tar 12 för marginal.
        public int[] g_out_vol  = new int[12];
        public int[] g_freq_out = new int[12];

        // Används av din read_init() loop (j <= 10)
        public bool[] g_master_chk = new bool[11];
        public int[]  g_master_vol = new int[11];

        public md_music()
        {
            // Default: full volym, “påslaget”
            for (int i = 0; i < g_out_vol.Length; i++)
                g_out_vol[i] = 1;

            for (int i = 0; i < g_freq_out.Length; i++)
                g_freq_out[i] = 0;

            for (int i = 0; i < g_master_chk.Length; i++)
                g_master_chk[i] = true;

            for (int i = 0; i < g_master_vol.Length; i++)
                g_master_vol[i] = 100;
        }
    }
}
