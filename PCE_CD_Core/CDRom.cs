using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ePceCD
{
    [Serializable]
    public class CDRom
    {
        // 常量定义
        private const int SECTOR_SIZE = 2352;
        private const int DATA_SECTOR_OFFSET = 16;
        private const int MODE1_DATA_SIZE = 2048;

        // SCSI状态管理
        private byte[] CMDBuffer = new byte[16];
        private int CMDBufferIndex = 0;
        private int CMDLength;
        private bool[] Signals = new bool[9];

        [NonSerialized]
        public MemoryStream dataBuffer;

        public int dataOffset;
        private int _readLogCount;
        private int _readSumCount;
        private int _cdRegLogCount;
        private byte messageByte;
        private int currentSector = -1;
        private int lastDataSector = -1;
        private FileStream _subFile;
        private long _subSectors;

        // CD 播放
        private int AudioSS, AudioES, AudioCS;
        private bool CdPlaying;
        private CDLOOPMODE CdLoopMode;
        private enum CDLOOPMODE { LOOP, IRQ, STOP };
        byte[] CDSBuffer = new byte[SECTOR_SIZE];
        private int _cdSectorOffsetBytes = -1;
        private int _cdSectorDataOffset = DATA_SECTOR_OFFSET;

        // 光盘数据结构
        public enum TrackType { AUDIO, MODE1, MODE2, MODE1_2352 }

        [Serializable]
        public struct PosMSF
        {
            public int MSF_M;
            public int MSF_S;
            public int MSF_F;
        }

        [Serializable]
        public class CDTrack
        {
            public int Number;
            public TrackType Type;
            public string FileName;
            [NonSerialized]
            public FileStream File;
            public long SectorStart;
            public long SectorEnd;
            public long OffsetStart;
            public long OffsetEnd;
            public bool IsLeadIn;
            public long LeadInSectorStart;
            public bool HasIndex0;
            public bool HasPregap;
            public long PregapLength;
            public PosMSF LeadIn;
            public PosMSF StartPos;
            public PosMSF EndPos;
            public byte Control;  // 子通道Q控制字段
            public byte Adr;      // 子通道Q ADR类型
            public bool IsWave;
            public bool AudioBigEndian;
            public bool AudioEndianDetected;
        }
        public CDTrack currentTrack, FileTrack;
        private bool _bramInitialized;

        public enum CdRomIrqSource
        {
            Adpcm = 0x04,
            Stop = 0x08,
            DataTransferDone = 0x20,
            DataTransferReady = 0x40
        }

        private enum ScsiSignal
        {
            Ack, Atn, Bsy, Cd, Io, Msg, Req, Rst, Sel
        }

        private enum ScsiCommand
        {
            TestUnitReady = 0x00,
            RequestSense = 0x03,
            Read = 0x08,
            AudioStartPos = 0xD8,
            AudioEndPos = 0xD9,
            AudioPause = 0xDA,
            ReadSubCodeQ = 0xDD,
            ReadToc = 0xDE,
        }

        private enum ScsiPhase
        {
            CMD, DataIn, Status, MessageIn, BusFree
        }
        private ScsiPhase _ScsiPhase;

        // IRQ和状态管理
        public byte EnabledIrqs, ActiveIrqs, ResetRegValue;
        private bool bramLocked;

        public List<CDTrack> tracks = new List<CDTrack>();

        public ADPCM _ADPCM;
        public AUDIOFADE _AUDIOFADE = new AUDIOFADE();
        private float _psgMix;
        private float _cdMix;
        private bool? _cdAudioEndianOverride;

        [NonSerialized]
        public BUS Bus;

        public CDRom(BUS bus)
        {
            _ADPCM = new ADPCM(this);
            Bus = bus;
            InitMixes();
        }

        public CDRom()
        {
            InitMixes();
        }

        private void InitMixes()
        {
            _psgMix = GetEnvMix("EUTHERDRIVE_PCE_PSG_MIX", 1.0f);
            _cdMix = GetEnvMix("EUTHERDRIVE_PCE_CD_MIX", 0.25f);
            _cdAudioEndianOverride = GetEnvEndianOverride();
        }

        public void RebindAfterDeserialize(BUS bus)
        {
            Bus = bus;
            _ADPCM.BindCdRom(this);
        }

        public bool IRQPending()
        {
            return (EnabledIrqs & ActiveIrqs) != 0;
        }

        private short SoftClip(int sample)
        {
            const int threshold = short.MaxValue - 1000;
            if (sample > threshold) return (short)(threshold + (sample - threshold) * 0.5f);
            if (sample < -threshold) return (short)(-threshold + (sample + threshold) * 0.5f);
            return (short)sample;
        }

        public void MixCD(short[] Buffer, int len, int offset = 0)
        {
            if (!CdPlaying) return;

            int samplesMixed = 0;
            while (samplesMixed < len)
            {
                if (AudioCS > AudioES)
                {
                    switch (CdLoopMode)
                    {
                        case CDLOOPMODE.STOP:
                            CdPlaying = false;
                            return;
                        case CDLOOPMODE.LOOP:
                            AudioCS = AudioSS;
                            _cdSectorOffsetBytes = -1;
                            break;
                        case CDLOOPMODE.IRQ:
                            ActiveIrqs |= (byte)CdRomIrqSource.Stop;
                            CdPlaying = false;
                            return;
                    }
                }
                var track = tracks.FirstOrDefault(t => t.SectorStart <= AudioCS && t.SectorEnd > AudioCS);
                if (track == null)
                    return;
                if (track.File == null)
                    return;
                if (_cdSectorOffsetBytes < 0 || _cdSectorOffsetBytes >= SECTOR_SIZE)
                {
                    long relSector = AudioCS - track.SectorStart;
                    long fileOffset = track.OffsetStart + relSector * SECTOR_SIZE;
                    track.File.Seek(fileOffset, SeekOrigin.Begin);
                    track.File.Read(CDSBuffer, 0, SECTOR_SIZE);
                    _cdSectorDataOffset = track.Type == TrackType.AUDIO ? 0 : DATA_SECTOR_OFFSET;
                    _cdSectorOffsetBytes = _cdSectorDataOffset;
                }

                while (_cdSectorOffsetBytes + 3 < SECTOR_SIZE && samplesMixed < len)
                {
                    int i = _cdSectorOffsetBytes;
                    short sampleL;
                    short sampleR;
                    bool bigEndian = track.AudioBigEndian;
                    if (_cdAudioEndianOverride.HasValue)
                        bigEndian = _cdAudioEndianOverride.Value;
                    if (track.Type == TrackType.AUDIO && bigEndian)
                    {
                        sampleL = (short)((CDSBuffer[i] << 8) | CDSBuffer[i + 1]);
                        sampleR = (short)((CDSBuffer[i + 2] << 8) | CDSBuffer[i + 3]);
                    }
                    else
                    {
                        sampleL = (short)((CDSBuffer[i + 1] << 8) | CDSBuffer[i]);
                        sampleR = (short)((CDSBuffer[i + 3] << 8) | CDSBuffer[i + 2]);
                    }

                    int mixedR = (int)(Buffer[offset + samplesMixed] * _psgMix + sampleR * _cdMix);
                    int mixedL = (int)(Buffer[offset + samplesMixed + 1] * _psgMix + sampleL * _cdMix);

                    Buffer[offset + samplesMixed++] = SoftClip(mixedR);
                    Buffer[offset + samplesMixed++] = SoftClip(mixedL);
                    _cdSectorOffsetBytes += 4;
                }

                if (_cdSectorOffsetBytes >= SECTOR_SIZE)
                {
                    _cdSectorOffsetBytes = -1;
                    AudioCS++;
                }
            }
        }

        private static float GetEnvMix(string name, float fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
                return fallback;
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        private static bool? GetEnvEndianOverride()
        {
            string raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CD_ENDIAN");
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim().ToLowerInvariant();
            if (raw == "auto")
                return null;
            if (raw == "little" || raw == "le")
                return false;
            if (raw == "big" || raw == "be")
                return true;
            return null;
        }

        private int MSFToLBA(int m, int s, int f) => m * 60 * 75 + s * 75 + f;
        private byte ToBCD(int value) => (byte)(((value / 10) << 4) | (value % 10));
        private byte FromBCD(byte value) { return (byte)(((value >> 4) & 0x0F) * 10 + (value & 0x0F)); }

        #region CUE文件解析
        public void LoadCue(string cuePath)
        {
            tracks.Clear();
            string baseDir = Path.GetDirectoryName(cuePath);

            foreach (string line in File.ReadLines(cuePath))
            {
                string[] parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                switch (parts[0].ToUpper())
                {
                    case "FILE":
                        ParseFileCommand(parts, baseDir);
                        break;
                    case "TRACK":
                        ParseTrackCommand(parts);
                        break;
                    case "INDEX":
                        ParseIndexCommand(parts);
                        break;
                    case "PREGAP":
                        HandlePregap(parts[1]);
                        break;
                }
            }
            CalculateTrackMSF();
            // Default to Geargrafx-style CUE calc; allow opt-out with EUTHERDRIVE_PCE_CUE_GEARGRAFX=0
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CUE_GEARGRAFX") != "0")
                CalculateTrackMSF_Geargrafx();
            DetectAudioEndianness();
            TryLoadSub(cuePath);
            Console.WriteLine($"Loaded {tracks.Count} tracks");
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TOC_DUMP") == "1")
                DumpToc();
        }

        private void DumpToc()
        {
            Console.WriteLine("[PCE-TOC] ---");
            foreach (var track in tracks)
            {
                string leadIn = track.IsLeadIn ? track.LeadInSectorStart.ToString() : "-";
                Console.WriteLine(
                    $"[PCE-TOC] Track {track.Number:00} {track.Type} Start {track.SectorStart} End {track.SectorEnd} " +
                    $"LeadIn {leadIn} Offset 0x{track.OffsetStart:X}");
            }
        }

        private void TryLoadSub(string cuePath)
        {
            _subFile?.Dispose();
            _subFile = null;
            _subSectors = 0;

            string subPath = Path.ChangeExtension(cuePath, ".sub");
            if (!File.Exists(subPath) && FileTrack?.FileName != null)
                subPath = Path.ChangeExtension(FileTrack.FileName, ".sub");

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CDROM SUB probe {subPath}");

            if (!File.Exists(subPath))
                return;

            try
            {
                _subFile = new FileStream(subPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (_subFile.Length % 96 == 0)
                    _subSectors = _subFile.Length / 96;
                Console.WriteLine($"CDROM {subPath} SUB LOADED");
            }
            catch
            {
                _subFile = null;
                _subSectors = 0;
            }
        }

        private bool _currentFileIsWave;

        private void ParseFileCommand(string[] parts, string baseDir)
        {
            string filename = string.Join(" ", parts.Skip(1).TakeWhile(p => p != "BINARY" && p != "WAVE")).Trim('"');
            string fileType = parts.LastOrDefault() ?? string.Empty;
            string filePath = Path.Combine(baseDir, filename);
            currentTrack = new CDTrack { File = new FileStream(filePath, FileMode.Open, FileAccess.Read) };
            currentTrack.FileName = filePath;
            string typeLabel = fileType.Equals("WAVE", StringComparison.OrdinalIgnoreCase) ? "WAVE" : "BINARY";
            _currentFileIsWave = typeLabel == "WAVE";
            Console.WriteLine($"CDROM {filePath} {typeLabel} LOADED");
        }

        private static string GetSaveDirectory()
        {
            string? overrideDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SAVE_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;
            return Path.Combine(Directory.GetCurrentDirectory(), "saves", "pce");
        }

        private void ParseTrackCommand(string[] parts)
        {
            var track = new CDTrack { File = currentTrack.File };
            track.FileName = track.File.Name;
            track.Number = int.Parse(parts[1]);
            track.IsWave = _currentFileIsWave;

            switch (parts[2])
            {
                case "AUDIO":
                    track.Type = TrackType.AUDIO;
                    track.Control = 0x00;
                    track.Adr = 0x01;
                    break;
                case "MODE1/2048":
                    track.Type = TrackType.MODE1;
                    track.Control = 0x40;
                    track.Adr = 0x01;
                    EnsureDataTrack(track);
                    break;
                case "MODE1/2352":
                    track.Type = TrackType.MODE1_2352;
                    track.Control = 0x40;
                    track.Adr = 0x01;
                    EnsureDataTrack(track);
                    break;
            }
            tracks.Add(track);
            currentTrack = track;
        }

        private void EnsureDataTrack(CDTrack track)
        {
            if (FileTrack != null)
                return;
            FileTrack = track;
            if (_bramInitialized || Bus.BRAM != null)
                return;

            string savefile = Path.GetFileNameWithoutExtension(track.FileName);
            string saveDir = GetSaveDirectory();
            Directory.CreateDirectory(saveDir);
            Bus.BRAM = new SaveMemoryBank(Path.Combine(saveDir, savefile));
            _bramInitialized = true;
        }

        private void ParseIndexCommand(string[] parts)
        {
            var msf = parts[2].Split(':').Select(int.Parse).ToArray();
            switch (parts[1])
            {
                case "00":
                    currentTrack.LeadIn = new PosMSF { MSF_M = msf[0], MSF_S = msf[1], MSF_F = msf[2] };
                    currentTrack.LeadInSectorStart = MSFToLBA(msf[0], msf[1], msf[2]);
                    currentTrack.HasIndex0 = true;
                    break;
                case "01":
                    currentTrack.StartPos = new PosMSF { MSF_M = msf[0], MSF_S = msf[1], MSF_F = msf[2] };
                    currentTrack.SectorStart = MSFToLBA(msf[0], msf[1], msf[2]);
                    break;
            }
        }

        private void HandlePregap(string msf)
        {
            var pregap = msf.Split(':').Select(int.Parse).ToArray();
            currentTrack.PregapLength = MSFToLBA(pregap[0], pregap[1], pregap[2]);
            currentTrack.HasPregap = true;
        }

        private void CalculateTrackMSF()
        {
            long fileOffset = 0;
            long discSectorCursor = 0;
            string lastFile = string.Empty;
            foreach (var track in tracks)
            {
                bool sameFile = string.Equals(track.FileName, lastFile, StringComparison.OrdinalIgnoreCase);
                if (!sameFile)
                    fileOffset = 0;
                lastFile = track.FileName ?? string.Empty;

                long baseOffset = GetFileDataOffset(track.FileName);
                int sectorSize = GetTrackSectorSize(track);
                long trackStartLba = MSFToLBA(track.StartPos.MSF_M, track.StartPos.MSF_S, track.StartPos.MSF_F);

                track.SectorStart = sameFile ? trackStartLba : (discSectorCursor + trackStartLba);
                long fileStartLba = sameFile ? 0 : trackStartLba;
                track.OffsetStart = baseOffset + fileOffset + (fileStartLba * sectorSize);

                var nextTrack = tracks.FirstOrDefault(t => t.Number == track.Number + 1);
                long sectorLength;
                if (nextTrack != null && string.Equals(nextTrack.FileName, track.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    long nextStartLba = MSFToLBA(nextTrack.StartPos.MSF_M, nextTrack.StartPos.MSF_S, nextTrack.StartPos.MSF_F);
                    sectorLength = Math.Max(0, nextStartLba - trackStartLba);
                }
                else
                {
                    long availableBytes = Math.Max(0, track.File.Length - baseOffset - fileOffset);
                    sectorLength = (availableBytes / sectorSize) - fileStartLba;
                    if (sectorLength < 0)
                        sectorLength = 0;
                }

                track.SectorEnd = track.SectorStart + sectorLength;
                track.OffsetEnd = track.OffsetStart + sectorLength * sectorSize;

                fileOffset += sectorLength * sectorSize;
                discSectorCursor = track.SectorEnd;

                track.EndPos = new PosMSF
                {
                    MSF_M = (int)(track.SectorEnd / (60 * 75)),
                    MSF_S = (int)((track.SectorEnd / 75) % 60),
                    MSF_F = (int)(track.SectorEnd % 75)
                };
            }
        }

        private void CalculateTrackMSF_Geargrafx()
        {
            if (tracks.Count == 0)
                return;

            long discSectorCursor = 0;
            int index = 0;

            while (index < tracks.Count)
            {
                string fileName = tracks[index].FileName ?? string.Empty;
                int fileStartIndex = index;
                long baseOffset = GetFileDataOffset(fileName);
                long fileOffset = 0;
                long totalPregap = 0;

                while (index < tracks.Count && string.Equals(tracks[index].FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    index++;

                for (int i = fileStartIndex; i < index; i++)
                {
                    var track = tracks[i];
                    int sectorSize = GetTrackSectorSize(track);
                    long index1Lba = MSFToLBA(track.StartPos.MSF_M, track.StartPos.MSF_S, track.StartPos.MSF_F);

                    if (track.HasPregap)
                        totalPregap += track.PregapLength;

                    track.SectorStart = index1Lba + totalPregap + discSectorCursor;

                    if (track.HasPregap)
                    {
                        track.IsLeadIn = true;
                        track.LeadInSectorStart = track.SectorStart - track.PregapLength;
                    }
                    else if (track.HasIndex0)
                    {
                        track.IsLeadIn = true;
                        track.LeadInSectorStart = track.LeadInSectorStart + discSectorCursor;
                    }

                    if (i != fileStartIndex)
                    {
                        var prev = tracks[i - 1];
                        long prevEnd = track.IsLeadIn ? (track.LeadInSectorStart - 1) : (track.SectorStart - 1);
                        if (prevEnd < prev.SectorStart)
                            prevEnd = prev.SectorStart;
                        prev.SectorEnd = prevEnd;
                        long prevSectors = prev.SectorEnd - prev.SectorStart + 1;
                        fileOffset = prev.OffsetStart + prevSectors * GetTrackSectorSize(prev);
                        prev.OffsetEnd = prev.OffsetStart + prevSectors * GetTrackSectorSize(prev);
                        tracks[i - 1] = prev;
                    }

                    track.OffsetStart = baseOffset + fileOffset;
                    if (track.IsLeadIn && !track.HasPregap)
                        track.OffsetStart += (track.SectorStart - track.LeadInSectorStart) * sectorSize;

                    tracks[i] = track;
                }

                var last = tracks[index - 1];
                int lastSectorSize = GetTrackSectorSize(last);
                long availableBytes = Math.Max(0, last.File.Length - last.OffsetStart);
                long sectorCount = availableBytes / lastSectorSize;
                if (availableBytes % lastSectorSize != 0)
                    sectorCount++;
                if (sectorCount < 0)
                    sectorCount = 0;

                last.SectorEnd = last.SectorStart + sectorCount - 1;
                last.OffsetEnd = last.OffsetStart + sectorCount * lastSectorSize;
                tracks[index - 1] = last;

                discSectorCursor = last.SectorEnd + 1;
            }

            foreach (var track in tracks)
            {
                track.EndPos = new PosMSF
                {
                    MSF_M = (int)(track.SectorEnd / (60 * 75)),
                    MSF_S = (int)((track.SectorEnd / 75) % 60),
                    MSF_F = (int)(track.SectorEnd % 75)
                };
            }
        }

        private static long GetFileDataOffset(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return 0;
            if (!fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return 0;
            try
            {
                using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                if (fs.Length < 12)
                    return 0;
                uint riff = br.ReadUInt32();
                uint riffSize = br.ReadUInt32();
                uint wave = br.ReadUInt32();
                if (riff != 0x46464952 || wave != 0x45564157)
                    return 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    uint chunkId = br.ReadUInt32();
                    uint chunkSize = br.ReadUInt32();
                    if (chunkId == 0x61746164)
                        return fs.Position;
                    fs.Position += chunkSize;
                    if ((chunkSize & 1) != 0)
                        fs.Position += 1;
                }
            }
            catch
            {
                return 0;
            }
            return 0;
        }
        #endregion

        private void DetectAudioEndianness()
        {
            foreach (var track in tracks)
            {
                if (track.Type != TrackType.AUDIO || track.File == null)
                    continue;
                if (_cdAudioEndianOverride.HasValue)
                {
                    track.AudioBigEndian = _cdAudioEndianOverride.Value;
                    track.AudioEndianDetected = true;
                    continue;
                }
                if (track.IsWave)
                {
                    track.AudioBigEndian = false;
                    track.AudioEndianDetected = true;
                    continue;
                }
                track.AudioBigEndian = DetectAudioBigEndian(track);
                track.AudioEndianDetected = true;
            }
        }

        private bool DetectAudioBigEndian(CDTrack track)
        {
            try
            {
                if (track.File == null)
                    return false;
                long oldPos = track.File.Position;
                byte[] buf = new byte[SECTOR_SIZE];
                long diffLeTotal = 0;
                long diffBeTotal = 0;
                int samples = 0;
                int maxSectors = 64;

                for (int sector = 0; sector < maxSectors; sector++)
                {
                    long offset = Math.Max(0, track.OffsetStart + (long)sector * SECTOR_SIZE);
                    if (offset + SECTOR_SIZE > track.File.Length)
                        break;
                    track.File.Seek(offset, SeekOrigin.Begin);
                    int read = track.File.Read(buf, 0, buf.Length);
                    if (read < 4)
                        break;

                    long diffLe = 0;
                    long diffBe = 0;
                    short prevLe = (short)((buf[1] << 8) | buf[0]);
                    short prevBe = (short)((buf[0] << 8) | buf[1]);
                    int limit = read & ~1;
                    for (int i = 2; i < limit; i += 2)
                    {
                        short sLe = (short)((buf[i + 1] << 8) | buf[i]);
                        short sBe = (short)((buf[i] << 8) | buf[i + 1]);
                        diffLe += Math.Abs(sLe - prevLe);
                        diffBe += Math.Abs(sBe - prevBe);
                        prevLe = sLe;
                        prevBe = sBe;
                    }

                    if (diffLe == 0 && diffBe == 0)
                        continue; // silent sector, skip

                    diffLeTotal += diffLe;
                    diffBeTotal += diffBe;
                    samples++;
                    if (samples >= 8)
                        break;
                }

                track.File.Seek(oldPos, SeekOrigin.Begin);

                if (samples == 0)
                    return true; // default to big-endian if all sampled sectors are silent

                if (diffBeTotal < diffLeTotal * 0.95)
                    return true;
                if (diffLeTotal < diffBeTotal * 0.95)
                    return false;
                return true;
            }
            catch
            {
                return true;
            }
        }

        #region SCSI核心逻辑
        private void SetPhase(ScsiPhase phase)
        {
            _ScsiPhase = phase;
            Array.Clear(Signals, 0, 9);
            switch (phase)
            {
                case ScsiPhase.CMD:
                    Signals[(int)ScsiSignal.Bsy] = true;
                    Signals[(int)ScsiSignal.Cd] = true;
                    Signals[(int)ScsiSignal.Msg] = false;
                    Signals[(int)ScsiSignal.Io] = false;
                    Signals[(int)ScsiSignal.Req] = true;
                    break;

                case ScsiPhase.DataIn:
                    Signals[(int)ScsiSignal.Bsy] = true;
                    Signals[(int)ScsiSignal.Io] = true;
                    if (dataBuffer != null && dataBuffer.Length > 0)
                        Signals[(int)ScsiSignal.Req] = true;
                    break;

                case ScsiPhase.Status:
                    Signals[(int)ScsiSignal.Bsy] = true;
                    Signals[(int)ScsiSignal.Io] = true;
                    Signals[(int)ScsiSignal.Cd] = true;
                    Signals[(int)ScsiSignal.Req] = true;
                    break;

                case ScsiPhase.MessageIn:
                    Signals[(int)ScsiSignal.Bsy] = true;
                    Signals[(int)ScsiSignal.Io] = true;
                    Signals[(int)ScsiSignal.Cd] = true;
                    Signals[(int)ScsiSignal.Msg] = true;
                    Signals[(int)ScsiSignal.Req] = true;
                    break;

                case ScsiPhase.BusFree:
                    Signals[(int)ScsiSignal.Bsy] = false;
                    Signals[(int)ScsiSignal.Req] = false;
                    break;
            }
            UpdateScsiIrqs();
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_PHASE_LOG") == "1")
                LogPhase(phase);
        }

        private int _phaseLogCount = 0;
        private void LogPhase(ScsiPhase phase)
        {
            if (_phaseLogCount >= 64)
                return;
            _phaseLogCount++;
            Console.WriteLine($"CD-ROM: Phase {phase} bsy={Signals[(int)ScsiSignal.Bsy]} req={Signals[(int)ScsiSignal.Req]} cd={Signals[(int)ScsiSignal.Cd]} io={Signals[(int)ScsiSignal.Io]} msg={Signals[(int)ScsiSignal.Msg]}");
        }

        private void UpdateScsiIrqs()
        {
            bool active = Signals[(int)ScsiSignal.Bsy] && Signals[(int)ScsiSignal.Io] && Signals[(int)ScsiSignal.Req];
            if (active)
            {
                if (Signals[(int)ScsiSignal.Cd])
                {
                    ActiveIrqs = (byte)((ActiveIrqs | (byte)CdRomIrqSource.DataTransferDone) &
                        unchecked((byte)~(byte)CdRomIrqSource.DataTransferReady));
                }
                else
                {
                    ActiveIrqs = (byte)((ActiveIrqs | (byte)CdRomIrqSource.DataTransferReady) &
                        unchecked((byte)~(byte)CdRomIrqSource.DataTransferDone));
                }
            }
            else
            {
                ActiveIrqs &= unchecked((byte)~((byte)CdRomIrqSource.DataTransferReady | (byte)CdRomIrqSource.DataTransferDone));
            }
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_IRQ_LOG") == "1")
                LogIrq();
        }

        private int _irqLogCount = 0;
        private void LogIrq()
        {
            if (_irqLogCount >= 200)
                return;
            _irqLogCount++;
            Console.WriteLine($"CD-ROM: IRQ state active=0x{ActiveIrqs:X2} enabled=0x{EnabledIrqs:X2} phase={_ScsiPhase}");
        }
        private void PrepareResponse(byte[] data)
        {
            dataBuffer?.Dispose();
            _readLogCount = 0;
            if (_ScsiPhase == ScsiPhase.Status)
            {
                dataBuffer = new MemoryStream(data, 0, 1, writable: false, publiclyVisible: true);
                dataBuffer.WriteByte(data.Length > 0 ? data[0] : (byte)0);
            }
            else
            {
                dataBuffer = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
            }
            dataOffset = 0;
        }

        private void SendStatus(byte status)
        {
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CD-ROM: SendStatus 0x{status:X2} phase={_ScsiPhase} dataOffset={dataOffset} dataLen={(dataBuffer != null ? dataBuffer.Length : 0)}");
            PrepareResponse(new byte[] { status });
            SetPhase(ScsiPhase.Status);

            System.Timers.Timer endTimer = new System.Timers.Timer(GetScsiDelayMs());
            endTimer.Elapsed += (s, e) =>
            {
                FinishCommand();
                endTimer.Dispose();
            };
            endTimer.AutoReset = false;
            endTimer.Start();
        }

        private void FinishCommand()
        {
            if (_ScsiPhase != ScsiPhase.Status) return;

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine("CD-ROM: FinishCommand -> MessageIn");
            SetPhase(ScsiPhase.MessageIn);
            messageByte = 0x00;
            PrepareResponse(new byte[] { messageByte });

            System.Timers.Timer busFreeTimer = new System.Timers.Timer(GetScsiDelayMs());
            busFreeTimer.Elapsed += (s, e) =>
            {

                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                    Console.WriteLine("CD-ROM: FinishCommand -> BusFree");
                SetPhase(ScsiPhase.BusFree);
                busFreeTimer.Dispose();
            };
            busFreeTimer.AutoReset = false;
            busFreeTimer.Start();
        }

        private static double GetScsiDelayMs()
        {
            return Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_FAST") == "1" ? 1 : 100;
        }

        public byte ReadDataPort()
        {
            if (dataBuffer == null || dataBuffer.Position >= dataBuffer.Length)
                return 0x00;

            int value = dataBuffer.ReadByte();
            if (value == -1)
                return 0x00;

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1" && _readLogCount < 4)
            {
                Console.WriteLine($"CD-ROM: ReadData byte=0x{value:X2} pos={dataBuffer.Position}/{dataBuffer.Length} phase={_ScsiPhase}");
                _readLogCount++;
            }

            dataOffset++;
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_REQ_PER_BYTE") == "1" &&
                (_ScsiPhase == ScsiPhase.DataIn || _ScsiPhase == ScsiPhase.MessageIn))
            {
                // Pulse REQ for each byte: drop after read, re-assert if more data remains
                Signals[(int)ScsiSignal.Req] = false;
                if (dataOffset < dataBuffer.Length)
                    Signals[(int)ScsiSignal.Req] = true;
            }
            if (dataOffset >= dataBuffer.Length)
            {
                if (_ScsiPhase == ScsiPhase.DataIn)
                {
                    SendStatus(0);
                }
                else if (_ScsiPhase == ScsiPhase.MessageIn &&
                         Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_MSGIN_EOF") == "1")
                {
                    SetPhase(ScsiPhase.BusFree);
                }
                else
                {
                    FinishCommand();
                }
            }

            return (byte)value;
        }

        public void WriteDataPort(byte value)
        {
            if (_ScsiPhase == ScsiPhase.CMD)
            {
                CMDBuffer[CMDBufferIndex++] = value;
                if (CMDBufferIndex == 1)
                    CMDLength = ScsiCMDLength((ScsiCommand)value);

                if (CMDBufferIndex >= CMDLength)
                {
                    ProcessCommand();
                    CMDBufferIndex = 0;
                }
            }
        }

        private int ScsiCMDLength(ScsiCommand cmd)
        {
            switch (cmd)
            {
                case ScsiCommand.TestUnitReady: return 6;
                case ScsiCommand.RequestSense: return 10;
                case ScsiCommand.Read: return 6;
                case ScsiCommand.ReadToc: return 10;
                case ScsiCommand.ReadSubCodeQ: return 10;
                default: return 10;
            }
        }

        private void ProcessCommand()
        {
            var cmd = (ScsiCommand)CMDBuffer[0];
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CMD_LOG") == "1")
                LogCommand();
            try
            {
                switch (cmd)
                {
                    case ScsiCommand.TestUnitReady:
                        HandleTestUnitReady();
                        break;
                    case ScsiCommand.RequestSense:
                        HandleRequestSense();
                        break;
                    case ScsiCommand.Read:
                        HandleRead6();
                        break;
                    case ScsiCommand.ReadToc:
                        HandleReadToc();
                        break;
                    case ScsiCommand.ReadSubCodeQ:
                        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                            Console.WriteLine("CD-ROM: ReadSubCodeQ");
                        HandleSubChannelQ();
                        break;
                    case ScsiCommand.AudioStartPos:
                        AudioStartPos();
                        break;
                    case ScsiCommand.AudioEndPos:
                        AudioEndPos();
                        break;
                    case ScsiCommand.AudioPause:
                        Console.WriteLine($"CD-ROM: AudioPause");
                        CdPlaying = false;
                        _cdSectorOffsetBytes = -1;
                        SendStatus(0);
                        break;
                    default:
                        Console.WriteLine($"CD-ROM: Unsupported SCSI command: 0x{cmd:X}");
                        SendStatus(0);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CD-ROM: SCSI Error: {ex.Message}");
                SendStatus(0x02);
            }
        }

        private int _cmdLogCount = 0;
        private void LogCommand()
        {
            if (_cmdLogCount >= 100)
                return;
            _cmdLogCount++;
            int len = CMDLength > 0 ? CMDLength : 6;
            byte[] buf = new byte[len];
            Array.Copy(CMDBuffer, buf, len);
            Console.WriteLine($"CD-ROM: CMD {buf[0]:X2} len={len} buf={BitConverter.ToString(buf)}");
        }

        private void HandleTestUnitReady()
        {
            Console.WriteLine($"CD-ROM: TestUnitReady");

            SendStatus(0x00);
        }

        private void HandleRequestSense()
        {
            byte[] senseData = { 0x70, 0x00, 0x06, 0x00, 0x00, 0x00 };

            PrepareResponse(senseData);

            SetPhase(ScsiPhase.DataIn);

            Console.WriteLine($"CD-ROM: RequestSense");
        }

        private void HandleRead6()
        {
            currentSector = CMDBuffer[3] | (CMDBuffer[2] << 8) | ((CMDBuffer[1] & 0x1F) << 16);
            int sectorsToRead = CMDBuffer[4];
            if (sectorsToRead == 0)
                sectorsToRead = 256;
            byte[] sectorBuffer = new byte[SECTOR_SIZE];
            bool strict = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_READ6_STRICT") == "1";

            Console.WriteLine($"CD-ROM: ReadSector {currentSector} to {sectorsToRead + currentSector - 1}");
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CD-ROM: READ6 lba={currentSector} count={sectorsToRead} cmd={BitConverter.ToString(CMDBuffer, 0, CMDLength > 0 ? CMDLength : 6)}");
            bool logFirstRead = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_READ_DUMP") == "1";
            bool dumped = false;
            long datasize = 0;
            currentTrack = tracks.FirstOrDefault(t => currentSector >= t.SectorStart && currentSector + sectorsToRead <= t.SectorEnd);
            if (currentTrack == null)
                currentTrack = FileTrack;
            int ssize = (currentTrack.Type == TrackType.AUDIO) ? SECTOR_SIZE : MODE1_DATA_SIZE;
            byte[] data = new byte[ssize * sectorsToRead];
            do
            {
                var track = tracks.FirstOrDefault(t =>
                {
                    long start = t.SectorStart;
                    if (strict && t.IsLeadIn)
                        start = t.LeadInSectorStart;
                    return start <= currentSector && t.SectorEnd > currentSector;
                }) ?? currentTrack;
                if (track.File == null)
                    break;
                lastDataSector = currentSector;
                if (strict && currentSector >= track.SectorEnd)
                {
                    SendStatus(0x02);
                    SetPhase(ScsiPhase.Status);
                    return;
                }
                int sectorSize = GetTrackSectorSize(track);
                int dataOffset = GetTrackDataOffset(track);
                long relSector = currentSector - track.SectorStart;
                long fileOffset = track.OffsetStart + relSector * sectorSize;
                if (strict && (fileOffset < 0 || fileOffset + sectorSize > track.File.Length))
                {
                    SendStatus(0x02);
                    SetPhase(ScsiPhase.Status);
                    return;
                }
                track.File.Seek(fileOffset, SeekOrigin.Begin);
                track.File.Read(sectorBuffer, 0, sectorSize);
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_READ_SUM") == "1" && _readSumCount < 4)
                {
                    int sumLen = track.Type == TrackType.AUDIO ? sectorSize : MODE1_DATA_SIZE;
                    int sumOff = track.Type == TrackType.AUDIO ? 0 : dataOffset;
                    uint sum = 0;
                    for (int i = 0; i < sumLen; i++)
                        sum = unchecked(sum + sectorBuffer[sumOff + i]);
                    Console.WriteLine($"CD-ROM: READ6 sum lba={currentSector} track={track.Number} len={sumLen} sum=0x{sum:X8}");
                    _readSumCount++;
                }
                if (logFirstRead && !dumped)
                {
                    int headCount = Math.Min(32, sectorSize);
                    byte[] head = new byte[headCount];
                    Array.Copy(sectorBuffer, 0, head, 0, headCount);
                    Console.WriteLine($"CD-ROM: READ6 head={BitConverter.ToString(head)} dataOff={dataOffset}");
                    dumped = true;
                }
                switch (track.Type)
                {
                    case TrackType.MODE1:
                        Array.Copy(sectorBuffer, dataOffset, data, datasize, MODE1_DATA_SIZE);
                        datasize += MODE1_DATA_SIZE;
                        break;
                    case TrackType.MODE1_2352:
                        Array.Copy(sectorBuffer, dataOffset, data, datasize, MODE1_DATA_SIZE);
                        datasize += MODE1_DATA_SIZE;
                        break;
                    default:
                        Array.Copy(sectorBuffer, 0, data, datasize, SECTOR_SIZE);
                        datasize += SECTOR_SIZE;
                        break;
                }
                currentSector++;
                sectorsToRead--;
            } while (sectorsToRead > 0);

            PrepareResponse(data);

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CD-ROM: READ6 prepared {data.Length} bytes");

            SetPhase(ScsiPhase.DataIn);

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CD-ROM: DATAIN req={Signals[(int)ScsiSignal.Req]} io={Signals[(int)ScsiSignal.Io]} cd={Signals[(int)ScsiSignal.Cd]} bsy={Signals[(int)ScsiSignal.Bsy]}");
        }

        private void HandleReadToc()
        {
            byte format = CMDBuffer[1]; // 参数
            byte trackNumber = FromBCD(CMDBuffer[2]); // 曲目号
            bool toc8 = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TOC_8B") == "1";
            byte[] toc = new byte[toc8 ? 8 : 4];
            int pos = 0;
            int minutes = 0, seconds = 0, frames = 0;
            long calcLBA;
            bool respectMsf = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TOC_MSF") == "1";
            bool msf = true;
            if (respectMsf)
                msf = (CMDBuffer[1] & 0x02) != 0;

            void WriteAddr(long lba, bool includeCtrlAdr)
            {
                if (msf)
                {
                    long addr = lba + 150;
                    minutes = (int)(addr / (60 * 75));
                    seconds = (int)((addr / 75) % 60);
                    frames = (int)(addr % 75);
                    toc[pos++] = ToBCD(minutes);
                    toc[pos++] = ToBCD(seconds);
                    toc[pos++] = ToBCD(frames);
                }
                else
                {
                    toc[pos++] = (byte)((lba >> 16) & 0xFF);
                    toc[pos++] = (byte)((lba >> 8) & 0xFF);
                    toc[pos++] = (byte)(lba & 0xFF);
                }

                if (includeCtrlAdr)
                {
                    // NOTE: Enabling this flag caused some discs to fall back to the BIOS CD player menu
                    // (i.e., BIOS didn't detect a data track). Keep behind flag for now.
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TOC_CTRLADR") == "1")
                        toc[pos++] = (byte)(currentTrack.Control | currentTrack.Adr);
                    else
                        toc[pos++] = currentTrack.Type == TrackType.AUDIO ? (byte)0x00 : (byte)0x04;

                    if (toc8)
                    {
                        toc[pos++] = ToBCD(currentTrack.Number);
                        toc[pos++] = 0x00;
                        toc[pos++] = 0x00;
                        toc[pos++] = 0x00;
                    }
                }
                else
                {
                    toc[pos++] = 0x00;
                    if (toc8)
                    {
                        toc[pos++] = 0x00;
                        toc[pos++] = 0x00;
                        toc[pos++] = 0x00;
                        toc[pos++] = 0x00;
                    }
                }
            }

            switch (format)
            {
                case 0x00:
                    toc[pos++] = 0x01;
                    toc[pos++] = ToBCD(tracks.Count);
                    toc[pos++] = 0x00;
                    toc[pos++] = 0x00;
                    Console.WriteLine($"CD-ROM: ReadTOC TrackCount {tracks.Count}");
                    break;

                case 0x01:
                    calcLBA = tracks.Count > 0 ? tracks[^1].SectorEnd + 1 : 0;
                    WriteAddr(calcLBA, includeCtrlAdr: false);
                    if (msf)
                        Console.WriteLine($"CD-ROM: ReadTOC TotalTime {minutes}:{seconds}:{frames}");
                    break;

                case 0x02:
                    if (trackNumber == 0)
                        trackNumber = 1;

                    if (trackNumber > tracks.Count())
                    {
                        long leadOutLba = tracks.Count > 0 ? tracks[^1].SectorEnd + 1 : 0;
                        WriteAddr(leadOutLba, includeCtrlAdr: false);
                        if (msf)
                            Console.WriteLine($"CD-ROM: ReadTOC LeadOut {minutes}:{seconds}:{frames}");
                        break;
                    }

                    currentTrack = tracks.FirstOrDefault(t => t.Number == trackNumber);
                    if (currentTrack == null)
                    {
                        Console.WriteLine($"CD-ROM: Invalid track number {trackNumber} for ReadTOC");
                        currentTrack = FileTrack;
                        return;
                    }
                    calcLBA = currentTrack.SectorStart;
                    WriteAddr(calcLBA, includeCtrlAdr: true);
                    Console.WriteLine($"CD-ROM: ReadTOC Track {trackNumber} StartPos {currentTrack.SectorStart}");
                    break;

                default:
                    Console.WriteLine($"CD-ROM: Unsupported ReadTOC format {format:X}");
                    return;
            }

            PrepareResponse(toc);

            SetPhase(ScsiPhase.DataIn);
        }

        private void HandleSubChannelQ()
        {
            bool Playing = CdPlaying;
            CdPlaying = false;

            currentSector = CdPlaying ? AudioCS : (lastDataSector >= 0 ? lastDataSector : AudioCS);

            var track = tracks.FirstOrDefault(t => t.SectorStart <= currentSector && t.SectorEnd > currentSector);
            if (track == null)
            {
                Console.WriteLine("SubChannelQ Invalid LBA");
                SendStatus(0x00);
                return;
            }
            Console.WriteLine($"CD-ROM: SubChannelQ Track {track.Number} Sector {currentSector}");

            byte[] qData = new byte[10];
            bool usedSub = false;
            if (_subFile != null && _subSectors > 0 && currentSector >= 0 && currentSector < _subSectors)
            {
                byte[] sub = new byte[96];
                _subFile.Seek(currentSector * 96, SeekOrigin.Begin);
                int read = _subFile.Read(sub, 0, sub.Length);
                if (read == 96)
                {
                    bool any = false;
                    for (int i = 12; i < 22; i++)
                        any |= sub[i] != 0x00 && sub[i] != 0xFF;
                    if (any)
                    {
                        Array.Copy(sub, 12, qData, 0, 10);
                        usedSub = true;
                    }
                }
            }

            if (!usedSub)
            {
                bool fixQ = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SUBQ_FIXED") == "1";
                bool negRel = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SUBQ_NEG") == "1";
                int absLba = currentSector + 150;
                int relLba;
                int index = 1;
                bool relIsNeg = false;

                if (fixQ && track.IsLeadIn && currentSector < track.SectorStart)
                {
                    index = 0;
                    if (negRel)
                    {
                        relIsNeg = true;
                        relLba = (int)(track.SectorStart - currentSector);
                    }
                    else
                    {
                        relLba = (int)(currentSector - track.LeadInSectorStart);
                    }
                }
                else
                {
                    relLba = (int)(currentSector - track.SectorStart);
                }

                if (relLba < 0)
                    relLba = 0;

                byte controlAdr = (byte)((track.Type == TrackType.AUDIO) ? 0x01 : 0x41);

                if (fixQ)
                {
                    qData[0] = controlAdr;
                    qData[1] = ToBCD(track.Number);
                    qData[2] = ToBCD(index);
                    if (relIsNeg)
                    {
                        int rmin = relLba / (60 * 75);
                        int rsec = (relLba / 75) % 60;
                        int rfrm = relLba % 75;
                        qData[3] = (byte)(ToBCD(rmin) | 0x80);
                        qData[4] = ToBCD(rsec);
                        qData[5] = ToBCD(rfrm);
                    }
                    else
                    {
                        qData[3] = ToBCD(relLba / (60 * 75));
                        qData[4] = ToBCD((relLba / 75) % 60);
                        qData[5] = ToBCD(relLba % 75);
                    }
                    qData[6] = 0x00;
                    qData[7] = ToBCD(absLba / (60 * 75));
                    qData[8] = ToBCD((absLba / 75) % 60);
                    qData[9] = ToBCD(absLba % 75);
                }
                else
                {
                    qData[0] = (byte)(Playing ? 0 : 1);
                    qData[1] = controlAdr;
                    qData[2] = ToBCD(track.Number); // Track
                    qData[3] = ToBCD(1); // Index
                    qData[4] = ToBCD(relLba / (60 * 75));
                    qData[5] = ToBCD((relLba / 75) % 60);
                    qData[6] = ToBCD(relLba % 75);
                    qData[7] = ToBCD(absLba / (60 * 75));
                    qData[8] = ToBCD((absLba / 75) % 60);
                    qData[9] = ToBCD(absLba % 75);
                }
            }

            PrepareResponse(qData);

            SetPhase(ScsiPhase.DataIn);

            CdPlaying = Playing;
        }

        private static int GetTrackSectorSize(CDTrack track)
        {
            return track.Type == TrackType.MODE1 ? MODE1_DATA_SIZE : SECTOR_SIZE;
        }

        private static int GetTrackDataOffset(CDTrack track)
        {
            return track.Type == TrackType.MODE1_2352 ? DATA_SECTOR_OFFSET : 0;
        }

        private int AudioGetPos()
        {
            int audiosector = 0;
            switch (CMDBuffer[9] & 0xC0)
            {
                case 0x00:
                    audiosector = (CMDBuffer[3] << 16) | (CMDBuffer[4] << 8) | CMDBuffer[5];
                    break;
                case 0x40:
                    {
                        int Minutes = FromBCD(CMDBuffer[2]);
                        int Seconds = FromBCD(CMDBuffer[3]);
                        int Frames = FromBCD(CMDBuffer[4]);
                        audiosector = MSFToLBA(Minutes, Seconds, Frames) - 150;
                        break;
                    }
                case 0x80:
                    {
                        byte trackNumber = FromBCD(CMDBuffer[2]);
                        int sector = (int)tracks.FirstOrDefault(t => t.Number == trackNumber).SectorStart;
                        audiosector = sector >= 0 ? sector : 0;
                        break;
                    }
            }
            return audiosector;
        }

        private void AudioStartPos()
        {
            AudioSS = AudioGetPos();
            AudioCS = AudioSS;
            _cdSectorOffsetBytes = -1;
            Console.WriteLine($"CD-ROM: AudioStartPos [{AudioSS}]");
            if (CMDBuffer[1] == 0)
            {
                CdPlaying = false;
            }
            else
            {
                CdPlaying = true;
            }
            SendStatus(0x00);
        }

        private void AudioEndPos()
        {
            AudioES = AudioGetPos();
            CdPlaying = true;
            Console.WriteLine($"CD-ROM: AudioEndPos [{AudioES}] Mode {CMDBuffer[1]:X1}");
            switch (CMDBuffer[1])
            {
                case 0: CdPlaying = false; break;
                case 1: CdLoopMode = CDLOOPMODE.LOOP; break;
                case 2: CdLoopMode = CDLOOPMODE.IRQ; break;
                case 3: CdLoopMode = CDLOOPMODE.STOP; break;
            }
            SendStatus(0x00);
        }

        public byte ReadAt(int address)
        {
            byte ret = 0xFF;

            switch (address & 0xFF)
            {
                case 0x00:
                    ret = ScsiStatus();
                    // NOTE: ACK write-only mode caused BIOS "Just a moment" hang (no progress). Keep behind flag.
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ACK_WRITEONLY") == "1")
                    {
                        // ACK handled on IRQCTRL writes only
                    }
                    else if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ACK_STRICT") == "1")
                    {
                        if (Signals[(int)ScsiSignal.Ack])
                            ProcessACK();
                    }
                    else
                    {
                        ProcessACK();
                    }
                    break;

                case 0x01:
                    ret = ReadDataPort();
                    break;

                case 0x02:
                    ret = (byte)(EnabledIrqs | (Signals[(int)ScsiSignal.Ack] ? 0x80 : 0));
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                        Console.WriteLine($"CD-ROM: IRQCTRL read value=0x{ret:X2} enabled=0x{EnabledIrqs:X2} ack={(Signals[(int)ScsiSignal.Ack] ? 1 : 0)}");
                    break;

                case 0x03:
                    bramLocked = true;
                    ret = (byte)(ActiveIrqs | 0x10 | 0x02); //ActiveIrqs | 0x10 | (ReadRightChannel ? 0 : 0x02)
                    break;

                case 0x04:
                    ret = ResetRegValue;
                    break;

                case 0x07:
                    ret = (byte)(bramLocked ? 0 : 0x80);
                    break;

                case 0x08:
                    ret = ReadDataPort();
                    break;

                case 0x09:
                case 0x0A:
                case 0x0B:
                case 0x0C:
                case 0x0D:
                case 0x0E:
                    ret = _ADPCM.ReadData(address);
                    break;

                case 0x0F:
                    ret = _AUDIOFADE.ReadData(address);
                    break;

                case 0xC1:
                case 0xC2:
                case 0xC3:
                case 0xC5:
                case 0xC6:
                case 0xC7: //Magic Signature
                    byte[] Signature = { 0x00, 0xAA, 0x55, 0x03 };
                    ret = Signature[address & 0x03];
                    break;

                default:
                    Console.WriteLine("CD-ROM READ  ACCESS [ 0x{0:X} << 0x{1:X2} ]  |  CPU <0x{2:X}>", address, ret, HuC6280.CurrentPC);
                    break;
            }
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CDREG_LOG") == "1" && _cdRegLogCount < 200)
            {
                _cdRegLogCount++;
                Console.WriteLine($"CD-ROM: RD reg=0x{(address & 0xFF):X2} val=0x{ret:X2} PC=0x{HuC6280.CurrentPC:X4}");
            }
            //Console.WriteLine("CD-ROM READ  ACCESS [ 0x{0:X} << 0x{1:X2} ]  |  CPU <0x{2:X}>", address, ret, HuC6280.CurrentPC);
            return ret;
        }

        public void WriteAt(int address, byte value)
        {
            //Console.WriteLine($"CD-ROM WRITE ACCESS [ 0x{address:X} >> 0x{value:X2} ]");
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CDREG_LOG") == "1" && _cdRegLogCount < 200)
            {
                _cdRegLogCount++;
                Console.WriteLine($"CD-ROM: WR reg=0x{(address & 0xFF):X2} val=0x{value:X2} PC=0x{HuC6280.CurrentPC:X4}");
            }
            switch (address & 0xF)
            {
                case 0x00: // 状态/控制寄存器 处理硬件复位或其他控制信号
                    if ((value & 0x80) != 0 && _ScsiPhase != ScsiPhase.DataIn)
                    {
                        ResetController();
                        SetPhase(ScsiPhase.CMD);
                    }
                    break;

                case 0x01: // 数据端口
                    WriteDataPort(value);
                    break;

                case 0x02:
                    EnabledIrqs = (byte)(value & 0x7F);
                    Signals[(int)ScsiSignal.Ack] = (value & 0x80) != 0;
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ACK_WRITEONLY") == "1")
                    {
                        if ((value & 0x80) != 0)
                            ProcessACK();
                    }
                    else
                    {
                        ProcessACK();
                    }
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                        Console.WriteLine($"CD-ROM: IRQCTRL write value=0x{value:X2} enabled=0x{EnabledIrqs:X2} ack={(Signals[(int)ScsiSignal.Ack] ? 1 : 0)}");
                    break;

                case 0x4:
                    ResetRegValue = (byte)(value & 0xF);
                    if ((value & 0x02) != 0)
                    {
                        ActiveIrqs = 0;
                        bramLocked = false;
                        EnabledIrqs &= 0x8F;
                    }
                    break;

                case 0x07:
                    bramLocked = (value & 0x80) == 0;
                    Bus.BRAM.WriteProtect(bramLocked);
                    break;

                case 0x08:
                case 0x09:
                case 0x0A:
                case 0x0B:
                case 0x0C:
                case 0x0D:
                case 0x0E:
                    _ADPCM.WriteData(address, value);
                    break;

                case 0x0F:
                    _AUDIOFADE.WriteData(address, value);
                    break;

                default:
                    Console.WriteLine($"CD-ROM WRITE ACCESS [ 0x{address:X} >> 0x{value:X2} ]");
                    break;
            }
        }

        private void ResetController()
        {
            CMDBufferIndex = 0;
            dataBuffer?.Dispose();
            dataBuffer = null;
            SetPhase(ScsiPhase.BusFree);
            currentSector = -1;
            //Console.WriteLine("CD-ROM Controller Reset");
        }

        private void ProcessACK()
        {
            if (Signals[(int)ScsiSignal.Req] && Signals[(int)ScsiSignal.Ack])
            {
                Signals[(int)ScsiSignal.Req] = false;
                return;
            }
            if (Signals[(int)ScsiSignal.Ack]) return;

            Signals[(int)ScsiSignal.Req] = true;
        }

        private byte ScsiStatus()
        {
            byte status = 0;
            if (Signals[(int)ScsiSignal.Io]) status |= 0x08;
            if (Signals[(int)ScsiSignal.Cd]) status |= 0x10;
            if (Signals[(int)ScsiSignal.Msg]) status |= 0x20;
            if (Signals[(int)ScsiSignal.Req]) status |= 0x40;
            if (Signals[(int)ScsiSignal.Bsy]) status |= 0x80;
            return status;
        }
        #endregion
    }
}
