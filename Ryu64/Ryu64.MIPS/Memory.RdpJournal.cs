#if N64_RDP_JOURNAL
using System;

namespace Ryu64.MIPS
{
    // Diagnostic builds only. Callbacks run synchronously on the emulator thread.
    public interface IRdpJournalObserver
    {
        void RdramWritten(uint address, uint length);
        void BeforeCommand(Memory memory, uint[] words);
        void AfterCommand(Memory memory, int command);
    }

    public partial class Memory
    {
        public IRdpJournalObserver RdpJournal { get; set; }
        private bool _journalExecutingCommand;

        private void JournalBeforeCommand(uint address, bool xbus, int length)
        {
            if (RdpJournal == null) return;
            var words = new uint[length];
            // Use the real FIFO assembler, including recycled buffers and XBUS.
            for (int i = 0; i < length; i++) words[i] = ReadRdpCommandWord(address + (uint)i * 4, xbus);
            RdpJournal.BeforeCommand(this, words);
            _journalExecutingCommand = true;
        }

        public byte[] JournalHiddenBits => _rdpHiddenBits;
        public byte[] JournalTmem => _rdpTmem;
        public ushort[] JournalTlut => _rdpTlut;
        public long JournalCommandCount => _rdpCommandCount;
        public void JournalSaveStart(System.IO.BinaryWriter writer)
        {
            // At this hook the FIFO may contain a *complete* command, which is
            // intentionally invalid as a partial-command savestate. That command
            // is the journal's next record; serialize the pre-command render
            // state with an empty FIFO, then restore the live assembler exactly.
            int count = _rdpPendingWordCount;
            uint address = _rdpPendingCommandAddress;
            try { _rdpPendingWordCount = 0; _rdpPendingCommandAddress = 0; SaveState(writer); }
            finally { _rdpPendingWordCount = count; _rdpPendingCommandAddress = address; }
        }
        public uint[] JournalImage => new[] { _rdpColorImageAddress, _rdpColorImageWidth, _rdpColorImageSize, _rdpMaskImageAddress };
        public byte[][] JournalVi => new[] { VI_STATUS_REG_RW, VI_ORIGIN_REG_RW, VI_WIDTH_REG_RW, VI_INTR_REG_RW,
            VI_CURRENT_REG_RW, VI_BURST_REG_RW, VI_V_SYNC_REG_RW, VI_H_SYNC_REG_RW, VI_LEAP_REG_RW,
            VI_H_START_REG_RW, VI_V_START_REG_RW, VI_V_BURST_REG_RW, VI_X_SCALE_REG_RW, VI_Y_SCALE_REG_RW };

        public void JournalReplayCommand(uint[] words)
        {
            if (words.Length < 2 || words.Length != GetRdpCommandWordLength((int)(words[0] >> 24 & 63)))
                throw new ArgumentException("Invalid complete RDP command");
            // Commands are already assembled. Scratch DMEM avoids contaminating
            // RDRAM and handles captures that begin inside a buffered command.
            _rdpPendingWordCount = 0;
            _rdpPendingCommandAddress = 0;
            _rdpReadingPendingCommand = false;
            for (int i = 0; i < words.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(SP_MEM_RW.AsSpan(i * 4), words[i]);
            WriteBigEndianWord(DPC_STATUS_REG_R, ReadBigEndianWord(DPC_STATUS_REG_R) | DpcStatusXbusDmemDma);
            if (ExecuteRdpDisplayList(0, (uint)words.Length * 4) != words.Length * 4)
                throw new InvalidOperationException("Incomplete journal command replay");
        }
    }
}
#endif
