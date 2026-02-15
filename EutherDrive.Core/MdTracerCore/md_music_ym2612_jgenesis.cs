using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal sealed class JgYm2612
    {
        private readonly md_ym2612 _legacy;

        public JgYm2612(md_ym2612 legacy)
        {
            _legacy = legacy;
        }

        public byte Read(uint address)
        {
            return _legacy.read8(address);
        }

        public byte ReadStatus(bool clearOnRead)
        {
            return _legacy.ReadStatus(clearOnRead);
        }

        public void Write(uint address, byte value, string source)
        {
            _legacy.write8(address, value, source);
        }

        public void Start()
        {
            _legacy.YM2612_Start();
        }

        public void FullReset()
        {
            _legacy.FullReset();
        }

        public void MarkZ80SafeBootComplete()
        {
            _legacy.MarkZ80SafeBootComplete();
        }

        public void Update()
        {
            _legacy.YM2612_Update();
        }

        public void UpdateBatch(Span<short> dst, int frames)
        {
            _legacy.YM2612_UpdateBatch(dst, frames);
        }

        public void EnsureAdvanceEachFrame()
        {
            _legacy.EnsureAdvanceEachFrame();
        }

        public void TickTimersFromZ80Cycles(int z80Cycles)
        {
            _legacy.TickTimersFromZ80Cycles(z80Cycles);
        }

        public void FlushDacRateFrame(long frame)
        {
            _legacy.FlushDacRateFrame(frame);
        }

        public void ConsumeAudStatCounters(
            out int keyOn,
            out int fnum,
            out int param,
            out int dacCmd,
            out int dacDat)
        {
            _legacy.ConsumeAudStatCounters(out keyOn, out fnum, out param, out dacCmd, out dacDat);
        }

        public void FlushTimerStats(long frame)
        {
            _legacy.FlushTimerStats(frame);
        }

        public int DebugDacEnabled => _legacy.DebugDacEnabled;
        public int DebugDacData => _legacy.DebugDacData;
        public byte DebugLastYmAddr => _legacy.DebugLastYmAddr;
        public byte DebugLastYmVal => _legacy.DebugLastYmVal;
        public string DebugLastYmSource => _legacy.DebugLastYmSource;

        public void DumpRecentYmWrites(string tag, int limit)
        {
            _legacy.DumpRecentYmWrites(tag, limit);
        }
    }
}
