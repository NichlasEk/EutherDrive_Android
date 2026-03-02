namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_music
    {
        private readonly md_sn76489 g_md_sn76489 = new md_sn76489();
        private readonly JgYm2612 _jgYm = new JgYm2612();
        private readonly JgSn76489 _jgPsg = new JgSn76489();
        private int _psgNoiseGainPercent = 100;

        private JgYm2612 JgYm => _jgYm;

        public void reset()
        {
            JgYm.Start();
            g_md_sn76489.SN76489_Start();
            _jgPsg.Reset();
        }

        public void run(int cycles) { }

        public byte YmRead(uint address)
        {
            return JgYm.Read(address);
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

        public void YmAdvanceSystemCycles(long cycles, int explicitYmTicks = -1, int explicitPsgTicks = -1)
        {
            if (explicitYmTicks >= 0)
            {
                if (explicitYmTicks > 0)
                    JgYm.AdvanceYmTicks(explicitYmTicks);
            }
            else
            {
                JgYm.AdvanceSystemCycles(cycles);
            }

            if (explicitPsgTicks >= 0)
            {
                if (explicitPsgTicks > 0)
                    _jgPsg.AdvancePsgTicks(explicitPsgTicks, g_out_vol, _psgNoiseGainPercent);
            }
            else
            {
                _jgPsg.AdvanceSystemCycles(cycles, g_out_vol, _psgNoiseGainPercent);
            }
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
            _jgPsg.Write(value);
            // Keep legacy PSG state updated for stats/frequency UI.
            g_md_sn76489.write8(value);
        }

        public void PsgUpdate()
        {
            _ = _jgPsg.UpdateSample(g_out_vol, _psgNoiseGainPercent);
        }

        public int PsgUpdateSample()
        {
            return _jgPsg.UpdateSample(g_out_vol, _psgNoiseGainPercent);
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
            JgYm.FlushRuntimeStats(frame);
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
