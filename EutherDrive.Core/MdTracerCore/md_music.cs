namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_music
    {
        internal readonly md_ym2612 g_md_ym2612 = new md_ym2612();
        internal readonly md_sn76489 g_md_sn76489 = new md_sn76489();

        public void reset()
        {
            g_md_ym2612.YM2612_Start();
            g_md_sn76489.SN76489_Start();
        }

        public void run(int cycles) { }

        internal void FlushAudioStats(long frame)
        {
            if (!md_ym2612.AudStatEnabled)
            {
                g_md_ym2612.FlushTimerStats(frame);
                return;
            }

            g_md_ym2612.ConsumeAudStatCounters(
                out int keyOn, out int fnum, out int param, out int dacCmd, out int dacDat);
            int psgWrites = g_md_sn76489.ConsumeAudStatWrites();

            Console.WriteLine(
                $"[AUDSTAT] frame={frame} ym_keyon={keyOn} ym_fnum={fnum} ym_param={param} " +
                $"ym_dac_cmd={dacCmd} ym_dac_dat={dacDat} psg={psgWrites}");
            g_md_ym2612.FlushTimerStats(frame);
        }
    }
}
