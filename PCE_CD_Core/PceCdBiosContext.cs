using System;
using System.IO;
using System.Linq;
using ProjectPSX.IO;

namespace ePceCD
{
    internal sealed class PceCdBiosContext
    {
        private const int Mode1SectorBytes = 2048;
        private const int RawSectorBytes = 2352;
        private const int RawMode1DataOffset = 16;

        public PceCdBiosContext(
            BUS bus,
            HuC6280 cpu,
            PceCdBiosTrace trace,
            PceCdBiosCallCatalog catalog,
            ushort entryTarget,
            ushort entryCaller,
            PceCdBiosTransferType entryTransferType,
            bool hasEntryTransfer)
        {
            Bus = bus;
            Cpu = cpu;
            Trace = trace;
            Catalog = catalog;
            EntryTarget = entryTarget;
            EntryCaller = entryCaller;
            EntryTransferType = entryTransferType;
            HasEntryTransfer = hasEntryTransfer;
        }

        public BUS Bus { get; }
        public HuC6280 Cpu { get; }
        public CDRom CdRom => Bus.CDRom;
        public PceCdBiosTrace Trace { get; }
        public PceCdBiosCallCatalog Catalog { get; }
        public string DiscName => Bus.CDfile;
        public ushort EntryTarget { get; }
        public ushort EntryCaller { get; }
        public PceCdBiosTransferType EntryTransferType { get; }
        public bool HasEntryTransfer { get; }

        public void SetProgramCounter(ushort value) => Cpu.HleSetProgramCounter(value);
        public void SetAccumulator(byte value) => Cpu.HleSetA(value);
        public void SetX(byte value) => Cpu.HleSetX(value);
        public void SetY(byte value) => Cpu.HleSetY(value);
        public void SetStackPointer(byte value) => Cpu.HleSetS(value);
        public void SetProcessorStatus(byte value) => Cpu.HleSetP(value);
        public void SetInterruptDisable(bool value) => Cpu.HleSetInterruptDisable(value);

        public void SetMprMap(params byte[] values)
        {
            if (values == null || values.Length != 8)
                throw new ArgumentException("Expected exactly 8 MPR values.", nameof(values));

            for (int i = 0; i < values.Length; i++)
                Cpu.HleSetMpr(i, values[i]);
        }

        public void ReturnFromSubroutine() => Cpu.HleReturnFromSubroutine();
        public void ReturnFromInterrupt() => Cpu.HleReturnFromInterrupt();
        public byte ReadMemory8(ushort address) => Cpu.HleReadMemory(address);
        public byte ReadZeroPage8(byte address) => Cpu.HleReadZeroPage(address);

        public void WriteMemory8(ushort address, byte value, string reason = "hle_write8", bool trace = true)
        {
            Cpu.HleWriteMemory(address, value);
            if (trace)
                Trace.LogMemoryWrite(reason, address, new[] { value });
        }

        public void WriteZeroPage8(byte address, byte value, string reason = "hle_zp_write8", bool trace = true)
        {
            Cpu.HleWriteZeroPage(address, value);
            if (trace)
                Trace.LogMemoryWrite(reason, address, new[] { value });
        }

        public void WriteMemoryBlock(ushort address, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            ushort current = address;
            for (int i = 0; i < data.Length; i++)
            {
                Cpu.HleWriteMemory(current, data[i]);
                current++;
            }

            Trace.LogMemoryWrite("hle_write", address, data);
        }

        public byte[] ReadMode1Sectors(int lba, int sectorCount)
        {
            if (sectorCount <= 0)
                return Array.Empty<byte>();

            byte[] data = new byte[sectorCount * Mode1SectorBytes];
            for (int i = 0; i < sectorCount; i++)
            {
                byte[] sector = ReadMode1Sector(lba + i);
                Buffer.BlockCopy(sector, 0, data, i * Mode1SectorBytes, Mode1SectorBytes);
            }

            return data;
        }

        public void LoadMode1Sectors(int lba, int sectorCount, ushort destinationAddress, string profileName)
        {
            byte[] data = ReadMode1Sectors(lba, sectorCount);
            WriteMemoryBlock(destinationAddress, data);
            Trace.LogBootRead(profileName, lba, sectorCount, destinationAddress, data.Length);
        }

        public void StageMode1SectorStream(int lba, int sectorCount, string profileName)
        {
            byte[] data = ReadMode1Sectors(lba, sectorCount);
            Bus.BiosRuntimeState.StageCdData(data, profileName);
            Trace.LogCdDataStage(profileName, lba, sectorCount, data.Length);
        }

        public bool TryReadStagedCdData(out byte value, out int remaining, out string label)
        {
            return Bus.BiosRuntimeState.TryReadStagedCdData(out value, out remaining, out label);
        }

        public int GetFirstDataTrackLba()
        {
            CDRom.CDTrack? track = CdRom.tracks.FirstOrDefault(candidate =>
                candidate.Type == CDRom.TrackType.MODE1 || candidate.Type == CDRom.TrackType.MODE1_2352);
            if (track == null)
                throw new InvalidOperationException("Disc has no data track.");
            return (int)track.SectorStart;
        }

        private byte[] ReadMode1Sector(int lba)
        {
            CDRom.CDTrack? track = CdRom.tracks.FirstOrDefault(candidate =>
                candidate.SectorStart <= lba && candidate.SectorEnd >= lba);
            if (track == null)
                throw new InvalidOperationException($"No track found for sector {lba}.");
            if (track.Type != CDRom.TrackType.MODE1 && track.Type != CDRom.TrackType.MODE1_2352)
                throw new InvalidOperationException($"Sector {lba} belongs to non-data track {track.Type}.");
            if (string.IsNullOrWhiteSpace(track.FileName))
                throw new InvalidOperationException($"Track {track.Number} does not have a backing file.");

            int sectorSize = track.Type == CDRom.TrackType.MODE1 ? Mode1SectorBytes : RawSectorBytes;
            int dataOffset = track.Type == CDRom.TrackType.MODE1 ? 0 : RawMode1DataOffset;
            long relSector = lba - track.SectorStart;
            long fileOffset = track.OffsetStart + relSector * sectorSize + dataOffset;

            byte[] data = new byte[Mode1SectorBytes];
            using Stream file = VirtualFileSystem.OpenRead(track.FileName);
            file.Seek(fileOffset, SeekOrigin.Begin);

            int offset = 0;
            while (offset < data.Length)
            {
                int read = file.Read(data, offset, data.Length - offset);
                if (read <= 0)
                    throw new EndOfStreamException($"Short read while loading sector {lba}.");
                offset += read;
            }

            return data;
        }
    }
}
