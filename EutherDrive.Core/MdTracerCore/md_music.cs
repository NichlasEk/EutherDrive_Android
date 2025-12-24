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
    }
}
