using System;

namespace ePceCD
{
    [Serializable]
    public sealed class PceCdBiosRuntimeState
    {
        private byte[] _stagedCdData = Array.Empty<byte>();

        public bool HasIrq1Handler { get; set; }
        public ushort Irq1HandlerAddress { get; set; }
        public bool E01E3E04ReadyIssued { get; set; }
        public ushort LastEntryTarget { get; set; }
        public ushort LastEntryCaller { get; set; }
        public PceCdBiosTransferType LastEntryTransferType { get; set; }
        public byte StartupCommandOpcode { get; set; }
        public byte StartupCommandStatus { get; set; }
        public int StartupReadLba { get; set; }
        public int StartupReadSectorCount { get; set; }
        public int StagedCdDataOffset { get; set; }
        public string StagedCdDataLabel { get; set; } = string.Empty;
        public bool StagedCdDataReadLogged { get; set; }
        public bool StagedCdDataUnderflowLogged { get; set; }

        public int StagedCdDataRemaining => _stagedCdData.Length - StagedCdDataOffset;

        public void NoteEntry(ushort caller, ushort target, PceCdBiosTransferType type)
        {
            LastEntryCaller = caller;
            LastEntryTarget = target;
            LastEntryTransferType = type;
        }

        public bool TryGetEntry(ushort target, out ushort caller, out PceCdBiosTransferType type)
        {
            if (LastEntryTarget == target)
            {
                caller = LastEntryCaller;
                type = LastEntryTransferType;
                return true;
            }

            caller = 0;
            type = default;
            return false;
        }

        public void StageCdData(byte[] data, string label)
        {
            _stagedCdData = data ?? Array.Empty<byte>();
            StagedCdDataOffset = 0;
            StagedCdDataLabel = label ?? string.Empty;
            StagedCdDataReadLogged = false;
            StagedCdDataUnderflowLogged = false;
        }

        public bool TryReadStagedCdData(out byte value, out int remaining, out string label)
        {
            if (_stagedCdData.Length == 0 || StagedCdDataOffset >= _stagedCdData.Length)
            {
                value = 0x00;
                remaining = 0;
                label = StagedCdDataLabel;
                return false;
            }

            value = _stagedCdData[StagedCdDataOffset++];
            remaining = _stagedCdData.Length - StagedCdDataOffset;
            label = StagedCdDataLabel;
            return true;
        }

        public void NoteStartupCommand(byte opcode, byte status, int readLba, int readSectorCount)
        {
            StartupCommandOpcode = opcode;
            StartupCommandStatus = status;
            StartupReadLba = readLba;
            StartupReadSectorCount = readSectorCount;
        }

        public void Clear()
        {
            HasIrq1Handler = false;
            Irq1HandlerAddress = 0;
            E01E3E04ReadyIssued = false;
            LastEntryTarget = 0;
            LastEntryCaller = 0;
            LastEntryTransferType = default;
            StartupCommandOpcode = 0;
            StartupCommandStatus = 0;
            StartupReadLba = 0;
            StartupReadSectorCount = 0;
            _stagedCdData = Array.Empty<byte>();
            StagedCdDataOffset = 0;
            StagedCdDataLabel = string.Empty;
            StagedCdDataReadLogged = false;
            StagedCdDataUnderflowLogged = false;
        }
    }
}
