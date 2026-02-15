namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_music
    {
        private readonly md_sn76489 g_md_sn76489 = new md_sn76489();
        private readonly JgYm2612 _jgYm = new JgYm2612();
        private readonly JgSn76489 _jgPsg = new JgSn76489();
        private static readonly bool UseJgenesisPsg = ReadEnvDefaultOn("EUTHERDRIVE_PSG_JGENESIS");
        private int _psgNoiseGainPercent = 100;

        private JgYm2612 JgYm => _jgYm;

        public void reset()
        {
            JgYm.Start();
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
            return JgYm.Read(address);
        }

        public byte YmReadStatus(bool clearOnRead)
        {
            return JgYm.ReadStatus(clearOnRead);
        }

        public void YmWrite(uint address, byte value, string source)
        {
            JgYm.Write(address, value, source);
        }

        public void YmStart()
        {
            JgYm.Start();
        }

        public void YmFullReset()
        {
            JgYm.FullReset();
        }

        public void MarkZ80SafeBootComplete()
        {
            JgYm.MarkZ80SafeBootComplete();
        }

        public void YmUpdate()
        {
            JgYm.Update();
        }

        public void YmUpdateBatch(Span<short> dst, int frames)
        {
            JgYm.UpdateBatch(dst, frames);
        }

        public void YmEnsureAdvanceEachFrame()
        {
            JgYm.EnsureAdvanceEachFrame();
        }

        public void TickYmTimersFromZ80(int z80Cycles)
        {
            JgYm.TickTimersFromZ80Cycles(z80Cycles);
        }

        public void FlushDacRateFrame(long frame)
        {
            JgYm.FlushDacRateFrame(frame);
        }

        public int DebugDacEnabled => JgYm.DebugDacEnabled;
        public int DebugDacData => JgYm.DebugDacData;
        public byte DebugLastYmAddr => JgYm.DebugLastYmAddr;
        public byte DebugLastYmVal => JgYm.DebugLastYmVal;
        public string DebugLastYmSource => JgYm.DebugLastYmSource;

        public void DumpRecentYmWrites(string tag, int limit)
        {
            JgYm.DumpRecentYmWrites(tag, limit);
        }

        public void ConsumeAudStatCounters(
            out int keyOn,
            out int fnum,
            out int param,
            out int dacCmd,
            out int dacDat)
        {
            JgYm.ConsumeAudStatCounters(out keyOn, out fnum, out param, out dacCmd, out dacDat);
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

        public int ConsumePsgAudStatWrites()
        {
            return g_md_sn76489.ConsumeAudStatWrites();
        }

        internal void FlushAudioStats(long frame)
        {
            if (!JgYm.AudStatEnabled)
            {
                JgYm.FlushTimerStats(frame);
                return;
            }

            JgYm.ConsumeAudStatCounters(
                out int keyOn, out int fnum, out int param, out int dacCmd, out int dacDat);
            int psgWrites = g_md_sn76489.ConsumeAudStatWrites();

            Console.WriteLine(
                $"[AUDSTAT] frame={frame} ym_keyon={keyOn} ym_fnum={fnum} ym_param={param} " +
                $"ym_dac_cmd={dacCmd} ym_dac_dat={dacDat} psg={psgWrites}");
            JgYm.FlushTimerStats(frame);
        }
    }
}
