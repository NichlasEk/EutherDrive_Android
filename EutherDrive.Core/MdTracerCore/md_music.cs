namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_music
    {
        internal readonly md_ym2612 g_md_ym2612 = new md_ym2612();
        internal readonly md_sn76489 g_md_sn76489 = new md_sn76489();
        private readonly JgSn76489 _jgPsg = new JgSn76489();
        private static readonly bool UseJgenesisPsg = ReadEnvDefaultOn("EUTHERDRIVE_PSG_JGENESIS");
        private int _psgNoiseGainPercent = 100;

        public void reset()
        {
            g_md_ym2612.YM2612_Start();
            g_md_sn76489.SN76489_Start();
            if (UseJgenesisPsg)
                _jgPsg.Reset();
        }

        public void run(int cycles) { }

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public byte YmRead(uint address)
        {
            return g_md_ym2612.read8(address);
        }

        public byte YmReadStatus(bool clearOnRead)
        {
            return g_md_ym2612.ReadStatus(clearOnRead);
        }

        public void YmWrite(uint address, byte value, string source)
        {
            g_md_ym2612.write8(address, value, source);
        }

        public void YmStart()
        {
            g_md_ym2612.YM2612_Start();
        }

        public void YmFullReset()
        {
            g_md_ym2612.FullReset();
        }

        public void MarkZ80SafeBootComplete()
        {
            g_md_ym2612.MarkZ80SafeBootComplete();
        }

        public void YmUpdate()
        {
            g_md_ym2612.YM2612_Update();
        }

        public void YmUpdateBatch(Span<short> dst, int frames)
        {
            g_md_ym2612.YM2612_UpdateBatch(dst, frames);
        }

        public void YmEnsureAdvanceEachFrame()
        {
            g_md_ym2612.EnsureAdvanceEachFrame();
        }

        public void TickYmTimersFromZ80(int z80Cycles)
        {
            g_md_ym2612.TickTimersFromZ80Cycles(z80Cycles);
        }

        public void FlushDacRateFrame(long frame)
        {
            g_md_ym2612.FlushDacRateFrame(frame);
        }

        public void PsgWrite(byte value)
        {
            if (UseJgenesisPsg)
                _jgPsg.Write(value);
            // Keep legacy PSG state updated for stats/frequency UI.
            g_md_sn76489.write8(value);
        }

        public void PsgUpdate()
        {
            if (UseJgenesisPsg)
            {
                _ = _jgPsg.UpdateSample(g_out_vol, _psgNoiseGainPercent);
                return;
            }
            g_md_sn76489.SN76489_Update();
        }

        public int PsgUpdateSample()
        {
            if (UseJgenesisPsg)
                return _jgPsg.UpdateSample(g_out_vol, _psgNoiseGainPercent);
            return g_md_sn76489.SN76489_Update();
        }

        public void SetPsgNoiseGainPercent(int percent)
        {
            if (percent < 0) percent = 0;
            else if (percent > 200) percent = 200;
            _psgNoiseGainPercent = percent;
            g_md_sn76489.SetNoiseGainPercent(percent);
        }

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
