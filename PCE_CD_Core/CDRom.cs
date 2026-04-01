using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EutherDrive.Core;
using EutherDrive.Core.Savestates;
using ProjectPSX.IO;

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
        private int _cdRegLogLimit;
        private int _cmdLogLimit;
        private int _lastRead6ExpectedBytes;
        private int _lastRead6ConsumedBytes;
        private byte _scsiDataLatch;
        private bool _cdAudioSampleToggle;
        private short _cdAudioSample;
        private int _statusPollCount;
        private int _progressToken;
        private int _lastStatusProgressToken;
        private byte _lastStatusValue;
        private int _msgInE9dfPolls;
        private ScsiCommand _lastCmd;
        private int _lastCmdLen;
        private byte[] _lastCmdBuf = new byte[16];
        private byte messageByte;
        [NonSerialized]
        private int _busyStatusCyclesRemaining;
        [NonSerialized]
        private bool _busyStatusPending;
        [NonSerialized]
        private byte _busyStatusValue;
        [NonSerialized]
        private int _busyStatusMediaSector = -1;
        private int currentSector = -1;
        private int lastDataSector = -1;
        [NonSerialized]
        private Stream _subFile;
        private long _subSectors;
        [NonSerialized]
        private List<System.Timers.Timer> _scsiTimers = new List<System.Timers.Timer>();

        // CD 播放
        private int AudioSS, AudioES, AudioCS;
        private bool CdPlaying;
        private int _currentMediaSector;
        private CDLOOPMODE CdLoopMode;
        private enum CDLOOPMODE { LOOP, IRQ, STOP };
        private enum CdAudioState : byte { Playing = 0x00, Idle = 0x01, Paused = 0x02, Stopped = 0x03 }
        private CdAudioState _cdAudioState;
        [NonSerialized]
        private double _cdAudioCycleCounter;
        [NonSerialized]
        private short[] _cdAudioQueue = Array.Empty<short>();
        [NonSerialized]
        private int _cdAudioQueueRead;
        [NonSerialized]
        private int _cdAudioQueueWrite;
        [NonSerialized]
        private int _cdAudioQueueCount;
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
            public Stream File;
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

        private struct CdRegAccess
        {
            public bool IsWrite;
            public byte Reg;
            public byte Val;
            public ushort Pc;
            public ScsiPhase Phase;
        }

        private const int CdRegRingSize = 64;
        private readonly CdRegAccess[] _cdRegRing = new CdRegAccess[CdRegRingSize];
        private int _cdRegRingPos;
        private int _stallDumpCount;
        private bool _eb5eBusFreeDumped;
        private bool _busFreeEntryDumped;

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
            CMD, DataIn, Status, MessageIn, BusFree, Busy
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
            EnsureRuntimeState();
            InitMixes();
        }

        public CDRom()
        {
            EnsureRuntimeState();
            InitMixes();
        }

        private void EnsureRuntimeState()
        {
            _scsiTimers ??= new List<System.Timers.Timer>();
            _subFile = null;
            if (_cdAudioQueue == null || _cdAudioQueue.Length == 0)
                _cdAudioQueue = new short[32768];
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
            EnsureRuntimeState();
            _ADPCM.BindCdRom(this);
        }

        public void EnterIdleState()
        {
            lock (_scsiTimers)
            {
                for (int i = 0; i < _scsiTimers.Count; i++)
                {
                    try
                    {
                        _scsiTimers[i].Stop();
                        _scsiTimers[i].Dispose();
                    }
                    catch
                    {
                        // Best-effort cleanup.
                    }
                }
                _scsiTimers.Clear();
            }

            ActiveIrqs = 0;
            EnabledIrqs = 0;
            CdPlaying = false;
            _cdAudioState = CdAudioState.Idle;
            AudioCS = 0;
            AudioSS = 0;
            AudioES = 0;
            CdLoopMode = CDLOOPMODE.STOP;
            _currentMediaSector = 0;
            _cdAudioSampleToggle = false;
            _cdAudioSample = 0;
            _cdAudioCycleCounter = 0;
            _cdAudioQueueRead = 0;
            _cdAudioQueueWrite = 0;
            _cdAudioQueueCount = 0;
            _cdSectorOffsetBytes = -1;
            _busyStatusCyclesRemaining = 0;
            _busyStatusPending = false;
            _busyStatusValue = 0;
            _busyStatusMediaSector = -1;
            _lastRead6ConsumedBytes = 0;
            _lastRead6ExpectedBytes = 0;
            ResetController();
        }

        public void RestoreExternalFilesAfterDeserialize()
        {
            // Track files are restored by BUS.DeSerializable(); restore optional .sub sidecar here.
            _subFile?.Dispose();
            _subFile = null;

            string? baseFile = FileTrack?.FileName ?? currentTrack?.FileName ?? tracks.FirstOrDefault()?.FileName;
            if (string.IsNullOrWhiteSpace(baseFile))
            {
                _subSectors = 0;
                return;
            }

            string subPath = Path.ChangeExtension(baseFile, ".sub");
            if (!VirtualFileSystem.Exists(subPath))
            {
                _subSectors = 0;
                return;
            }

            try
            {
                _subFile = VirtualFileSystem.OpenRead(subPath);
                _subSectors = (_subFile.Length % 96 == 0) ? (_subFile.Length / 96) : 0;
            }
            catch
            {
                _subFile = null;
                _subSectors = 0;
            }
        }

        public bool IRQPending()
        {
            // Only IRQ request bits 2..6 are visible here (mask 0x7C).
            return (EnabledIrqs & ActiveIrqs & 0x7C) != 0;
        }

        public void AppendDeterminismTrace(StringBuilder sb)
        {
            if (sb == null)
                return;

            sb.Append(" cd_phase=").Append(_ScsiPhase);
            sb.Append(" cd_cmd=").Append(((byte)_lastCmd).ToString("X2"));
            sb.Append(" cd_cmdlen=").Append(_lastCmdLen);
            sb.Append(" cd_dataofs=").Append(dataOffset);
            sb.Append(" cd_bufpos=").Append(dataBuffer?.Position ?? -1);
            sb.Append(" cd_buflen=").Append(dataBuffer?.Length ?? 0);
            sb.Append(" cd_irqe=").Append(EnabledIrqs.ToString("X2"));
            sb.Append(" cd_irqa=").Append(ActiveIrqs.ToString("X2"));
            sb.Append(" cd_pending=").Append(IRQPending() ? 1 : 0);
            sb.Append(" cd_sig_req=").Append(Signals[(int)ScsiSignal.Req] ? 1 : 0);
            sb.Append(" cd_sig_ack=").Append(Signals[(int)ScsiSignal.Ack] ? 1 : 0);
            sb.Append(" cd_sig_bsy=").Append(Signals[(int)ScsiSignal.Bsy] ? 1 : 0);
            sb.Append(" cd_sig_cd=").Append(Signals[(int)ScsiSignal.Cd] ? 1 : 0);
            sb.Append(" cd_sig_io=").Append(Signals[(int)ScsiSignal.Io] ? 1 : 0);
            sb.Append(" cd_sig_msg=").Append(Signals[(int)ScsiSignal.Msg] ? 1 : 0);
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
            int samplesMixed = 0;
            while (samplesMixed < len)
            {
                if (_cdAudioQueueCount < 2)
                    break;

                short sampleR = DequeueCdSample();
                short sampleL = DequeueCdSample();

                int mixedR = (int)(Buffer[offset + samplesMixed] * _psgMix + sampleR * _cdMix);
                int mixedL = (int)(Buffer[offset + samplesMixed + 1] * _psgMix + sampleL * _cdMix);

                Buffer[offset + samplesMixed++] = SoftClip(mixedR);
                Buffer[offset + samplesMixed++] = SoftClip(mixedL);
            }
        }

        public void ClockAudio(int cycles)
        {
            if (cycles <= 0)
                return;

            if (_busyStatusPending)
            {
                if (_ScsiPhase != ScsiPhase.Busy)
                {
                    _busyStatusPending = false;
                    _busyStatusCyclesRemaining = 0;
                    _busyStatusMediaSector = -1;
                }
                else
                {
                    _busyStatusCyclesRemaining -= cycles;
                    if (_busyStatusCyclesRemaining <= 0)
                    {
                        _busyStatusPending = false;
                        _busyStatusCyclesRemaining = 0;
                        if (_busyStatusMediaSector >= 0)
                        {
                            _currentMediaSector = _busyStatusMediaSector;
                            _busyStatusMediaSector = -1;
                        }
                        if (TraceVerboseEnabled() && _lastCmd == ScsiCommand.AudioStartPos)
                        {
                            Console.WriteLine(
                                $"CD-ROM: AudioStartPos busy complete mediaSector={_currentMediaSector} audioCS={AudioCS} state={_cdAudioState}");
                        }
                        SendStatus(_busyStatusValue);
                    }
                }
            }

            _ADPCM.Clock(cycles);
            _cdAudioCycleCounter += cycles;
            double cyclesPerSample = GetCdAudioCyclesPerSample();

            while (_cdAudioCycleCounter >= cyclesPerSample)
            {
                _cdAudioCycleCounter -= cyclesPerSample;

                short sampleL = 0;
                short sampleR = 0;

                if (CdPlaying)
                {
                    GenerateNextCdSample(out sampleL, out sampleR);
                }

                EnqueueCdSample(sampleR);
                EnqueueCdSample(sampleL);
            }
        }

        private void GenerateNextCdSample(out short sampleL, out short sampleR)
        {
            sampleL = 0;
            sampleR = 0;

            if (!CdPlaying)
                return;

            if (AudioCS > AudioES)
            {
                if (CddaTraceEnabled())
                {
                    Console.WriteLine(
                        $"CD-ROM: CDDA stop-check audioCS={AudioCS} audioES={AudioES} loop={CdLoopMode} playing={CdPlaying} state={_cdAudioState}");
                }

                switch (CdLoopMode)
                {
                    case CDLOOPMODE.STOP:
                        CdPlaying = false;
                        _cdAudioState = CdAudioState.Stopped;
                        return;
                    case CDLOOPMODE.LOOP:
                        AudioCS = AudioSS;
                        _currentMediaSector = AudioCS;
                        _cdSectorOffsetBytes = -1;
                        break;
                    case CDLOOPMODE.IRQ:
                        CdPlaying = false;
                        _cdAudioState = CdAudioState.Stopped;
                        _cdSectorOffsetBytes = -1;
                        SendStatus(0x00);
                        return;
                }
            }

            var track = tracks.FirstOrDefault(t => t.SectorStart <= AudioCS && t.SectorEnd >= AudioCS);
            if (track == null || track.File == null)
                return;

            if (_cdSectorOffsetBytes < 0 || _cdSectorOffsetBytes >= SECTOR_SIZE)
            {
                _currentMediaSector = AudioCS;
                if (CddaTraceEnabled())
                {
                    Console.WriteLine(
                        $"CD-ROM: CDDA load sector={AudioCS} audioES={AudioES} offset={_cdSectorOffsetBytes} state={_cdAudioState}");
                }

                long relSector = AudioCS - track.SectorStart;
                long fileOffset = track.OffsetStart + relSector * SECTOR_SIZE;
                track.File.Seek(fileOffset, SeekOrigin.Begin);
                track.File.Read(CDSBuffer, 0, SECTOR_SIZE);
                _cdSectorDataOffset = track.Type == TrackType.AUDIO ? 0 : DATA_SECTOR_OFFSET;
                _cdSectorOffsetBytes = _cdSectorDataOffset;
            }

            if (_cdSectorOffsetBytes + 3 >= SECTOR_SIZE)
                return;

            int i = _cdSectorOffsetBytes;
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

            _cdSectorOffsetBytes += 4;
            if (_cdSectorOffsetBytes >= SECTOR_SIZE)
            {
                _cdSectorOffsetBytes = -1;
                AudioCS++;
                _currentMediaSector = AudioCS;
                if (CddaTraceEnabled())
                {
                    Console.WriteLine(
                        $"CD-ROM: CDDA advance nextSector={AudioCS} audioES={AudioES}");
                }
            }
        }

        private void EnqueueCdSample(short value)
        {
            if (_cdAudioQueue.Length == 0)
                return;

            if (_cdAudioQueueCount >= _cdAudioQueue.Length)
            {
                _cdAudioQueueRead = (_cdAudioQueueRead + 1) % _cdAudioQueue.Length;
                _cdAudioQueueCount--;
            }

            _cdAudioQueue[_cdAudioQueueWrite] = value;
            _cdAudioQueueWrite = (_cdAudioQueueWrite + 1) % _cdAudioQueue.Length;
            _cdAudioQueueCount++;
        }

        private short DequeueCdSample()
        {
            if (_cdAudioQueueCount <= 0 || _cdAudioQueue.Length == 0)
                return 0;

            short value = _cdAudioQueue[_cdAudioQueueRead];
            _cdAudioQueueRead = (_cdAudioQueueRead + 1) % _cdAudioQueue.Length;
            _cdAudioQueueCount--;
            return value;
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

            foreach (string line in ReadLines(cuePath))
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
            // Default to compatibility CUE calc; allow opt-out with EUTHERDRIVE_PCE_CUE_COMPAT=0
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CUE_COMPAT") != "0")
                CalculateTrackMSF_Compat();
            DetectAudioEndianness();
            TryLoadSub(cuePath);
            Console.WriteLine($"Loaded {tracks.Count} tracks");
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TOC_DUMP") == "1")
                DumpToc();
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            using var reader = new StreamReader(VirtualFileSystem.OpenRead(path));
            while (reader.ReadLine() is { } line)
                yield return line;
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
            if (!VirtualFileSystem.Exists(subPath) && FileTrack?.FileName != null)
                subPath = Path.ChangeExtension(FileTrack.FileName, ".sub");

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CDROM SUB probe {subPath}");

            if (!VirtualFileSystem.Exists(subPath))
                return;

            try
            {
                _subFile = VirtualFileSystem.OpenRead(subPath);
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
            string filePath = CueSheetResolver.ResolveReferencedPathFromDirectory(baseDir, filename);
            currentTrack = new CDTrack { File = VirtualFileSystem.OpenRead(filePath) };
            currentTrack.FileName = filePath;
            string typeLabel = fileType.Equals("WAVE", StringComparison.OrdinalIgnoreCase) ? "WAVE" : "BINARY";
            _currentFileIsWave = typeLabel == "WAVE";
            Console.WriteLine($"CDROM {filePath} {typeLabel} LOADED");
        }

        private static string GetSaveDirectory(string? contentPath)
        {
            return PersistentStoragePath.ResolveSaveDirectory(
                contentPath,
                "pce",
                Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SAVE_DIR"));
        }

        private void ParseTrackCommand(string[] parts)
        {
            var track = new CDTrack { File = currentTrack.File };
            track.FileName = currentTrack.FileName;
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
            string saveDir = GetSaveDirectory(track.FileName);
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

        private void CalculateTrackMSF_Compat()
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
                    {
                        long leadInDeltaSectors = track.SectorStart - track.LeadInSectorStart;
                        long offsetSectors = leadInDeltaSectors;

                        // Some split-bin dumps include less INDEX00 than the MSF delta suggests.
                        // Infer real file-start offset from file length when this is the first track in a file.
                        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_INDEX00_FILE_OFFSET_FIX") == "1" &&
                            i == fileStartIndex && i + 1 < tracks.Count &&
                            !string.Equals(tracks[i + 1].FileName, track.FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            long nextIndex1Lba = MSFToLBA(tracks[i + 1].StartPos.MSF_M, tracks[i + 1].StartPos.MSF_S, tracks[i + 1].StartPos.MSF_F);
                            long cueTrackSectors = Math.Max(0, nextIndex1Lba - index1Lba);
                            long fileTrackSectors = track.File.Length / sectorSize;
                            long extraSectorsInFile = fileTrackSectors - cueTrackSectors;
                            if (extraSectorsInFile >= 0 && extraSectorsInFile <= leadInDeltaSectors)
                                offsetSectors = extraSectorsInFile;
                        }

                        track.OffsetStart += offsetSectors * sectorSize;
                    }

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

            // Post-pass: for split-bin tracks with INDEX00 in a separate file, align file offset
            // to the actual extra sectors present in that file (instead of assuming full lead-in).
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_INDEX00_FILE_OFFSET_FIX") == "1")
            {
                for (int i = 0; i < tracks.Count - 1; i++)
                {
                    var track = tracks[i];
                    var next = tracks[i + 1];
                    if (!track.IsLeadIn)
                        continue;
                    if (string.Equals(track.FileName, next.FileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (track.File == null)
                        continue;

                    int sectorSize = GetTrackSectorSize(track);
                    long fileSectors = track.File.Length / sectorSize;
                    long logicalSectors = Math.Max(0, next.SectorStart - track.SectorStart);
                    long leadInDelta = Math.Max(0, track.SectorStart - track.LeadInSectorStart);
                    long extraSectors = fileSectors - logicalSectors;
                    if (extraSectors < 0 || extraSectors > leadInDelta)
                        continue;

                    long baseOffset = GetFileDataOffset(track.FileName);
                    track.OffsetStart = baseOffset + extraSectors * sectorSize;
                    long sectorCount = Math.Max(0, track.SectorEnd - track.SectorStart + 1);
                    track.OffsetEnd = track.OffsetStart + sectorCount * sectorSize;
                    tracks[i] = track;
                }
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
                using var fs = VirtualFileSystem.OpenRead(fileName);
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
            ScsiPhase previousPhase = _ScsiPhase;
            _ScsiPhase = phase;
            bool ackLevel = Signals[(int)ScsiSignal.Ack];
            Array.Clear(Signals, 0, 9);
            Signals[(int)ScsiSignal.Ack] = ackLevel;
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
                    // BusFree is an idle state; REQ must remain low.
                    Signals[(int)ScsiSignal.Req] = false;
                    break;

                case ScsiPhase.Busy:
                    Signals[(int)ScsiSignal.Bsy] = true;
                    Signals[(int)ScsiSignal.Req] = false;
                    break;
            }
            if (phase == ScsiPhase.BusFree && previousPhase == ScsiPhase.MessageIn && dataBuffer != null)
            {
                dataBuffer.Dispose();
                dataBuffer = null;
                dataOffset = 0;
            }
            if (!_busFreeEntryDumped &&
                phase == ScsiPhase.BusFree &&
                Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1")
            {
                _busFreeEntryDumped = true;
                Console.WriteLine("CD-ROM: BusFree entry snapshot");
                DumpCdRegRing();
            }
            MarkProgress();
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

        private bool TraceVerboseEnabled()
        {
            return Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VERBOSE") == "1";
        }

        private bool CddaTraceEnabled()
        {
            return Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CDDA_TRACE") == "1";
        }

        private static double GetCdAudioCyclesPerSample()
        {
            // BUS.tick() advances in HuC6280 CPU cycles at ~7.16 MHz.
            return 7159090.0 / 44100.0;
        }

        private void BeginBusyStatus(byte status, int cycles)
        {
            _busyStatusValue = status;
            _busyStatusCyclesRemaining = cycles < 0 ? 0 : cycles;
            _busyStatusPending = true;
            _busyStatusMediaSector = -1;
            SetPhase(ScsiPhase.Busy);
        }

        private static int GetBusyStatusDelayCycles()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_BUSY_STATUS_CYCLES");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int cycles) && cycles >= 0)
                return cycles;

            return -1;
        }

        private static int MillisecondsToCpuCycles(double ms)
        {
            if (ms <= 0)
                return 0;

            // BUS.Clock feeds HuC6280 CPU cycles at ~7.16 MHz into the CD logic.
            return (int)Math.Round(ms * (7159090.0 / 1000.0));
        }

        private readonly struct SeekSectorGroup
        {
            public SeekSectorGroup(int sectorsPerRevolution, int sectorStart, int sectorEnd, double rotationMs)
            {
                SectorsPerRevolution = sectorsPerRevolution;
                SectorStart = sectorStart;
                SectorEnd = sectorEnd;
                RotationMs = rotationMs;
            }

            public int SectorsPerRevolution { get; }
            public int SectorStart { get; }
            public int SectorEnd { get; }
            public double RotationMs { get; }
        }

        private static readonly SeekSectorGroup[] s_seekSectorGroups =
        {
            new(10, 0, 12572, 133.47),
            new(11, 12573, 30244, 146.82),
            new(12, 30245, 49523, 160.17),
            new(13, 49524, 70408, 173.51),
            new(14, 70409, 92900, 186.86),
            new(15, 92901, 116998, 200.21),
            new(16, 116999, 142703, 213.56),
            new(17, 142704, 170014, 226.90),
            new(18, 170015, 198932, 240.25),
            new(19, 198933, 229456, 253.60),
            new(20, 229457, 261587, 266.95),
            new(21, 261588, 295324, 280.29),
            new(22, 295325, 330668, 293.64),
            new(23, 330669, 333012, 306.99),
        };

        private static int SeekFindGroup(int sector)
        {
            for (int i = 0; i < s_seekSectorGroups.Length; i++)
            {
                SeekSectorGroup group = s_seekSectorGroups[i];
                if (sector >= group.SectorStart && sector <= group.SectorEnd)
                    return i;
            }

            return 0;
        }

        private static double GetCdSeekTimeMilliseconds(int startSector, int endSector)
        {
            int startIndex = SeekFindGroup(startSector);
            int targetIndex = SeekFindGroup(endSector);
            int sectorDifference = Math.Abs(endSector - startSector);
            double trackDifference;

            if (targetIndex == startIndex)
            {
                trackDifference = sectorDifference / (double)s_seekSectorGroups[targetIndex].SectorsPerRevolution;
            }
            else if (targetIndex > startIndex)
            {
                trackDifference =
                    (s_seekSectorGroups[startIndex].SectorEnd - startSector) /
                    (double)s_seekSectorGroups[startIndex].SectorsPerRevolution;
                trackDifference +=
                    (endSector - s_seekSectorGroups[targetIndex].SectorStart) /
                    (double)s_seekSectorGroups[targetIndex].SectorsPerRevolution;
                trackDifference += 1606.48 * (targetIndex - startIndex - 1);
            }
            else
            {
                trackDifference =
                    (startSector - s_seekSectorGroups[startIndex].SectorStart) /
                    (double)s_seekSectorGroups[startIndex].SectorsPerRevolution;
                trackDifference +=
                    (s_seekSectorGroups[targetIndex].SectorEnd - endSector) /
                    (double)s_seekSectorGroups[targetIndex].SectorsPerRevolution;
                trackDifference += 1606.48 * (startIndex - targetIndex - 1);
            }

            SeekSectorGroup targetGroup = s_seekSectorGroups[targetIndex];
            if (sectorDifference < 2)
                return (9.0 * 1000.0 / 60.0);
            if (sectorDifference < 5)
                return (9.0 * 1000.0 / 60.0) + (targetGroup.RotationMs / 2.0);
            if (trackDifference <= 80.0)
                return (18.0 * 1000.0 / 60.0) + (targetGroup.RotationMs / 2.0);
            if (trackDifference <= 160.0)
                return (22.0 * 1000.0 / 60.0) + (targetGroup.RotationMs / 2.0);
            if (trackDifference <= 644.0)
            {
                return (22.0 * 1000.0 / 60.0) +
                       (targetGroup.RotationMs / 2.0) +
                       ((trackDifference - 161.0) * 16.66 / 80.0);
            }

            return (48.0 * 1000.0 / 60.0) + ((trackDifference - 644.0) * 16.66 / 195.0);
        }

        private int GetAudioStartStatusDelayCycles(int startSector, int previousSector)
        {
            int overrideCycles = GetBusyStatusDelayCycles();
            if (overrideCycles >= 0)
                return overrideCycles;

            return MillisecondsToCpuCycles(GetCdSeekTimeMilliseconds(previousSector, startSector));
        }

        private int GetCdRegLogLimit()
        {
            if (_cdRegLogLimit > 0)
                return _cdRegLogLimit;

            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CDREG_LOG_LIMIT");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int limit) && limit > 0)
            {
                _cdRegLogLimit = limit;
                return _cdRegLogLimit;
            }

            _cdRegLogLimit = 200;
            return _cdRegLogLimit;
        }

        private int GetCmdLogLimit()
        {
            if (_cmdLogLimit > 0)
                return _cmdLogLimit;

            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CMD_LOG_LIMIT");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int limit) && limit > 0)
            {
                _cmdLogLimit = limit;
                return _cmdLogLimit;
            }

            _cmdLogLimit = 100;
            return _cmdLogLimit;
        }

        private void MarkProgress()
        {
            _progressToken++;
            _statusPollCount = 0;
            _lastStatusProgressToken = _progressToken;
        }

        private void MaybeLogStatusStall(byte statusValue)
        {
            bool verbose = TraceVerboseEnabled();

            if (_progressToken == _lastStatusProgressToken && statusValue == _lastStatusValue)
            {
                _statusPollCount++;
            }
            else
            {
                _statusPollCount = 1;
                _lastStatusValue = statusValue;
                _lastStatusProgressToken = _progressToken;
            }

            if (verbose && _statusPollCount == 2048)
            {
                string lastCmdHex = _lastCmdLen > 0
                    ? BitConverter.ToString(_lastCmdBuf, 0, _lastCmdLen)
                    : "n/a";
                string bufState = dataBuffer == null
                    ? "null"
                    : $"{dataBuffer.Position}/{dataBuffer.Length}";
                Console.WriteLine($"CD-ROM: STATUS STALL val=0x{statusValue:X2} phase={_ScsiPhase} pc=0x{HuC6280.CurrentPC:X4} cmd=0x{(byte)_lastCmd:X2} cmdbuf={lastCmdHex} buf={bufState} req={Signals[(int)ScsiSignal.Req]} ack={Signals[(int)ScsiSignal.Ack]} bsy={Signals[(int)ScsiSignal.Bsy]} cd={Signals[(int)ScsiSignal.Cd]} io={Signals[(int)ScsiSignal.Io]} msg={Signals[(int)ScsiSignal.Msg]} irqA=0x{ActiveIrqs:X2} irqE=0x{EnabledIrqs:X2}");
                if (_stallDumpCount < 2)
                {
                    _stallDumpCount++;
                    DumpCdRegRing();
                }
            }

        }

        private void RecordCdRegAccess(bool isWrite, int reg, byte val)
        {
            // Keep the ring useful during BIOS poll loops by collapsing repeated
            // status reads at the same PC/phase.
            if (!isWrite && (reg & 0xFF) == 0x00)
            {
                int prevIdx = (_cdRegRingPos - 1 + CdRegRingSize) % CdRegRingSize;
                CdRegAccess prev = _cdRegRing[prevIdx];
                if (!prev.IsWrite &&
                    prev.Reg == 0x00 &&
                    prev.Val == val &&
                    prev.Pc == (ushort)HuC6280.CurrentPC &&
                    prev.Phase == _ScsiPhase)
                {
                    return;
                }
            }

            _cdRegRing[_cdRegRingPos] = new CdRegAccess
            {
                IsWrite = isWrite,
                Reg = (byte)(reg & 0xFF),
                Val = val,
                Pc = (ushort)HuC6280.CurrentPC,
                Phase = _ScsiPhase
            };
            _cdRegRingPos = (_cdRegRingPos + 1) % CdRegRingSize;
        }

        private void DumpCdRegRing()
        {
            Console.WriteLine("CD-ROM: REG RING (newest last)");
            for (int i = 0; i < CdRegRingSize; i++)
            {
                int idx = (_cdRegRingPos + i) % CdRegRingSize;
                CdRegAccess entry = _cdRegRing[idx];
                if (entry.Pc == 0 && entry.Reg == 0 && entry.Val == 0 && entry.Phase == 0 && !entry.IsWrite)
                    continue;
                string rw = entry.IsWrite ? "WR" : "RD";
                Console.WriteLine($"  {rw} reg=0x{entry.Reg:X2} val=0x{entry.Val:X2} pc=0x{entry.Pc:X4} phase={entry.Phase}");
            }
        }

        private void UpdateScsiIrqs()
        {
            // Transfer IRQ bits are not phase-derived.
            // They are explicitly set/cleared by command handlers and ACK transitions.
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
            MarkProgress();
            dataBuffer = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
            dataOffset = 0;
        }

        private void SendStatus(byte status)
        {
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine($"CD-ROM: SendStatus 0x{status:X2} phase={_ScsiPhase} dataOffset={dataOffset} dataLen={(dataBuffer != null ? dataBuffer.Length : 0)}");
            // Command completion exposes transfer-done while STATUS is pending.
            // DataIn paths already latch this bit on the final byte, so this is mainly for
            // command-only completions such as AudioStartPos/AudioPause.
            ActiveIrqs |= (byte)CdRomIrqSource.DataTransferDone;
            PrepareResponse(new byte[] { status });
            SetPhase(ScsiPhase.Status);
        }

        private void FinishCommand()
        {
            if (_ScsiPhase != ScsiPhase.Status) return;

            // Experimental compatibility path:
            // Some BIOS command paths (notably 0xDE GetInfo/TOC) appear to expect
            // immediate BusFree after STATUS without a separate MessageIn byte.
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SKIP_MSGIN_FOR_DE") == "1" &&
                _lastCmd == ScsiCommand.ReadToc)
            {
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                    Console.WriteLine("CD-ROM: FinishCommand -> BusFree (skip MessageIn for 0xDE)");
                ActiveIrqs &= unchecked((byte)~(byte)CdRomIrqSource.DataTransferDone);
                SetPhase(ScsiPhase.BusFree);
                dataBuffer?.Dispose();
                dataBuffer = null;
                dataOffset = 0;
                return;
            }

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                Console.WriteLine("CD-ROM: FinishCommand -> MessageIn");
            SetPhase(ScsiPhase.MessageIn);
            messageByte = 0x00;
            PrepareResponse(new byte[] { messageByte });
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_DATA_LOG") == "1")
                Console.WriteLine($"CD-ROM: MSGIN byte=0x{messageByte:X2} pc=0x{HuC6280.CurrentPC:X4}");

            // Some BIOS loops pulse ACK once to leave STATUS and then only drop ACK low.
            // If ACK is already high when entering MessageIn, coalesce MessageIn ACK handling
            // so the controller can return to BusFree without requiring a second rising edge.
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_MSGIN_ACK_COALESCE") == "1" &&
                Signals[(int)ScsiSignal.Ack])
            {
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                    Console.WriteLine("CD-ROM: FinishCommand -> BusFree (coalesced MessageIn ACK)");
                ActiveIrqs &= unchecked((byte)~(byte)CdRomIrqSource.DataTransferDone);
                SetPhase(ScsiPhase.BusFree);
                dataBuffer?.Dispose();
                dataBuffer = null;
                dataOffset = 0;
            }
        }

        private void StartScsiTimer(Action callback)
        {
            System.Timers.Timer timer = new System.Timers.Timer(GetScsiDelayMs());
            lock (_scsiTimers)
            {
                _scsiTimers.Add(timer);
            }
            timer.AutoReset = false;
            timer.Elapsed += (s, e) =>
            {
                try
                {
                    callback();
                }
                finally
                {
                    lock (_scsiTimers)
                    {
                        _scsiTimers.Remove(timer);
                    }
                    timer.Dispose();
                }
            };
            timer.Start();
        }

        private static double GetScsiDelayMs()
        {
            string? custom = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_DELAY_MS");
            if (!string.IsNullOrWhiteSpace(custom) && double.TryParse(custom, out double ms) && ms >= 0)
                return ms;
            // Default to fast/near-immediate handshakes. Slow timing was causing boot-sensitive titles to stall.
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_FAST") == "0")
                return 100;
            return 1;
        }

        public byte ReadDataPort()
        {
            if (dataBuffer == null || dataBuffer.Position >= dataBuffer.Length)
            {
                // Status/message phase advances on ACK, not on data-port underflow.
                if (_ScsiPhase == ScsiPhase.DataIn)
                    SendStatus(0);
                return 0x00;
            }

            int value = dataBuffer.ReadByte();
            if (value == -1)
                return 0x00;

            MarkProgress();
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_DATA_LOG") == "1")
            {
                Console.WriteLine($"CD-ROM: RD DATA val=0x{value:X2} pos={dataBuffer.Position}/{dataBuffer.Length} phase={_ScsiPhase} pc=0x{HuC6280.CurrentPC:X4}");
            }
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1" && _readLogCount < 4)
            {
                Console.WriteLine($"CD-ROM: ReadData byte=0x{value:X2} pos={dataBuffer.Position}/{dataBuffer.Length} phase={_ScsiPhase}");
                _readLogCount++;
            }

            dataOffset++;
            if (_ScsiPhase == ScsiPhase.DataIn && _lastCmd == ScsiCommand.Read)
                _lastRead6ConsumedBytes++;
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_REQ_PER_BYTE") == "1" &&
                _ScsiPhase == ScsiPhase.DataIn)
            {
                // Pulse REQ for each byte: drop after read, re-assert if more data remains
                Signals[(int)ScsiSignal.Req] = false;
                if (dataOffset < dataBuffer.Length)
                    Signals[(int)ScsiSignal.Req] = true;
                UpdateScsiIrqs();
            }
            if (dataOffset >= dataBuffer.Length)
            {
                if (_ScsiPhase == ScsiPhase.DataIn)
                {
                    ActiveIrqs &= unchecked((byte)~(byte)CdRomIrqSource.DataTransferReady);
                    // Latch transfer-done on DataIn completion; cleared on MessageIn ACK.
                    ActiveIrqs |= (byte)CdRomIrqSource.DataTransferDone;
                    if (TraceVerboseEnabled() && _lastCmd == ScsiCommand.Read && _lastRead6ExpectedBytes > 0)
                        Console.WriteLine($"CD-ROM: READ6 consume {_lastRead6ConsumedBytes}/{_lastRead6ExpectedBytes} bytes before STATUS");
                    SendStatus(0);
                }
            }

            return (byte)value;
        }

        public void WriteDataPort(byte value)
        {
            // Accept bus data only when IO=0, then consume it on ACK.
            if (Signals[(int)ScsiSignal.Io])
                return;
            _scsiDataLatch = value;
        }

        private int ScsiCMDLength(ScsiCommand cmd)
        {
            // Match the controller framing:
            // commands below 0x20 are 6-byte CDBs, others are 10-byte CDBs.
            return (byte)cmd < 0x20 ? 6 : 10;
        }

        private void ProcessCommand()
        {
            var cmd = (ScsiCommand)CMDBuffer[0];
            _lastCmd = cmd;
            if (cmd != ScsiCommand.Read)
            {
                _lastRead6ExpectedBytes = 0;
                _lastRead6ConsumedBytes = 0;
            }
            _lastCmdLen = CMDLength > 0 ? CMDLength : 6;
            Array.Clear(_lastCmdBuf, 0, _lastCmdBuf.Length);
            Array.Copy(CMDBuffer, _lastCmdBuf, _lastCmdLen);
            MarkProgress();
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
                        _cdAudioState = CdAudioState.Paused;
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
            if (_cmdLogCount >= GetCmdLogLimit())
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
            currentTrack = tracks.FirstOrDefault(t => currentSector >= t.SectorStart && currentSector + sectorsToRead - 1 <= t.SectorEnd);
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
                    return start <= currentSector && t.SectorEnd >= currentSector;
                }) ?? currentTrack;
                if (track.File == null)
                    break;
                lastDataSector = currentSector;
                if (strict && currentSector > track.SectorEnd)
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
                bool logReadSum = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_READ_SUM") == "1" || TraceVerboseEnabled();
                if (logReadSum && _readSumCount < 8)
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
                _currentMediaSector = currentSector;
                sectorsToRead--;
            } while (sectorsToRead > 0);

            if (!CdPlaying)
                _cdAudioState = CdAudioState.Idle;

            PrepareResponse(data);
            _lastRead6ExpectedBytes = data.Length;
            _lastRead6ConsumedBytes = 0;
            ActiveIrqs |= (byte)CdRomIrqSource.DataTransferReady;

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
            bool preferFirstDataTrack = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_TOC_FIRST_DATA") == "1";
            int firstTrackNum = tracks.Count > 0 ? tracks[0].Number : 1;
            int firstDataTrackNum = tracks.FirstOrDefault(t => t.Type != TrackType.AUDIO)?.Number ?? firstTrackNum;
            int reportedFirstTrackNum = preferFirstDataTrack ? firstDataTrackNum : firstTrackNum;
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
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                        Console.WriteLine($"CD-ROM: ReadTOC first/last first={firstTrackNum} firstData={firstDataTrackNum} reported={reportedFirstTrackNum}");
                    toc[pos++] = ToBCD(reportedFirstTrackNum);
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
                        trackNumber = (byte)reportedFirstTrackNum;

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
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                    {
                        byte tocMode = currentTrack.Type == TrackType.AUDIO ? (byte)0x00 : (byte)0x04;
                        Console.WriteLine(
                            $"CD-ROM: ReadTOC trackInfo req={trackNumber} type={currentTrack.Type} ctrl=0x{currentTrack.Control:X2} adr=0x{currentTrack.Adr:X2} modeByte=0x{tocMode:X2}");
                    }
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
            int subqSector = _currentMediaSector >= 0
                ? _currentMediaSector
                : (CdPlaying ? AudioCS : (lastDataSector >= 0 ? lastDataSector : 0));

            if (subqSector < 0)
                subqSector = 0;

            if (_subFile != null && subqSector >= 0 && subqSector < _subSectors)
            {
                byte[] subFrame = new byte[96];
                try
                {
                    _subFile.Seek((long)subqSector * 96, SeekOrigin.Begin);
                    int read = _subFile.Read(subFrame, 0, subFrame.Length);
                    if (read == subFrame.Length)
                    {
                        byte[] rawQData = new byte[10];
                        rawQData[0] = (byte)_cdAudioState;
                        rawQData[1] = subFrame[12];
                        rawQData[2] = subFrame[13];
                        rawQData[3] = subFrame[14];
                        rawQData[4] = subFrame[15];
                        rawQData[5] = subFrame[16];
                        rawQData[6] = subFrame[17];
                        rawQData[7] = subFrame[19];
                        rawQData[8] = subFrame[20];
                        rawQData[9] = subFrame[21];

                        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                        {
                            Console.WriteLine(
                                $"CD-ROM: SubChannelQ raw sector={subqSector} audioCS={AudioCS} audioES={AudioES} playing={CdPlaying} state={_cdAudioState} q={BitConverter.ToString(rawQData)}");
                        }

                        PrepareResponse(rawQData);
                        SetPhase(ScsiPhase.DataIn);
                        return;
                    }
                }
                catch
                {
                    // Fall back to synthesized SubQ below.
                }
            }

            var track = tracks.FirstOrDefault(t => t.SectorStart <= subqSector && t.SectorEnd >= subqSector);
            if (track == null)
            {
                Console.WriteLine("SubChannelQ Invalid LBA");
                SendStatus(0x00);
                return;
            }
            Console.WriteLine($"CD-ROM: SubChannelQ Track {track.Number} Sector {subqSector}");

            byte[] qData = new byte[10];
            int relLba = subqSector - (int)track.SectorStart;
            if (relLba < 0)
                relLba = 0;

            qData[0] = (byte)_cdAudioState;
            qData[1] = (byte)(track.Type == TrackType.AUDIO ? 0x01 : 0x41);
            qData[2] = ToBCD(track.Number);
            qData[3] = 0x01;
            qData[4] = ToBCD(relLba / (60 * 75));
            qData[5] = ToBCD((relLba / 75) % 60);
            qData[6] = ToBCD(relLba % 75);
            int absoluteMsfSector = subqSector + 150;
            qData[7] = ToBCD(absoluteMsfSector / (60 * 75));
            qData[8] = ToBCD((absoluteMsfSector / 75) % 60);
            qData[9] = ToBCD(absoluteMsfSector % 75);

            PrepareResponse(qData);

            SetPhase(ScsiPhase.DataIn);
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
            int previousSector = _currentMediaSector >= 0
                ? _currentMediaSector
                : (CdPlaying
                    ? AudioCS
                    : (lastDataSector >= 0
                        ? lastDataSector
                        : (currentSector >= 0 ? currentSector : 0)));

            AudioSS = AudioGetPos();
            AudioCS = AudioSS;
            _cdSectorOffsetBytes = -1;
            CdLoopMode = CDLOOPMODE.STOP;

            var startTrack = tracks.FirstOrDefault(t => t.SectorStart <= AudioSS && t.SectorEnd >= AudioSS);
            if (startTrack != null)
                AudioES = (int)startTrack.SectorEnd;
            else if (tracks.Count > 0)
                AudioES = (int)tracks[tracks.Count - 1].SectorEnd;
            else
                AudioES = AudioSS;

            Console.WriteLine($"CD-ROM: AudioStartPos [{AudioSS}]");
            if (CMDBuffer[1] == 0)
            {
                CdPlaying = false;
                _cdAudioState = CdAudioState.Paused;
            }
            else
            {
                CdPlaying = true;
                _cdAudioState = CdAudioState.Playing;
            }
            BeginBusyStatus(0x00, GetAudioStartStatusDelayCycles(AudioSS, previousSector));
        }

        private void AudioEndPos()
        {
            AudioES = AudioGetPos();
            CdPlaying = true;
            _cdAudioState = CdAudioState.Playing;
            Console.WriteLine($"CD-ROM: AudioEndPos [{AudioES}] Mode {CMDBuffer[1]:X1}");
            if (TraceVerboseEnabled())
                Console.WriteLine($"CD-ROM: AudioEndPos IRQ pre active=0x{ActiveIrqs:X2} enabled=0x{EnabledIrqs:X2}");
            if (CddaTraceEnabled())
            {
                Console.WriteLine(
                    $"CD-ROM: AudioEndPos state audioSS={AudioSS} audioCS={AudioCS} audioES={AudioES} cmdMode={CMDBuffer[1]:X1}");
            }
            switch (CMDBuffer[1])
            {
                case 0:
                    CdPlaying = false;
                    _cdAudioState = CdAudioState.Stopped;
                    break;
                case 1: CdLoopMode = CDLOOPMODE.LOOP; break;
                case 2:
                    CdLoopMode = CDLOOPMODE.IRQ;
                    SetPhase(ScsiPhase.Busy);
                    return;
                case 3: CdLoopMode = CDLOOPMODE.STOP; break;
            }
            SendStatus(0x00);
        }

        public byte ReadAt(int address)
        {
            if (Bus?.ArcadeCard != null && address >= 0x1A00 && address <= 0x1AFF)
                return Bus.ArcadeCard.ReadHardware(address);

            byte ret = 0xFF;

            switch (address & 0xFF)
            {
                case 0x00:
                    ret = ScsiStatus();
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_MSGIN_E9DF_WATCHDOG") == "1" &&
                        _ScsiPhase == ScsiPhase.MessageIn &&
                        HuC6280.CurrentPC == 0xE9DF &&
                        (ret == 0xF8 || ret == 0xB8))
                    {
                        _msgInE9dfPolls++;
                        if (_msgInE9dfPolls >= 256)
                        {
                            if (ret == 0xF8)
                            {
                                // Compatibility watchdog: some BIOS paths can stop ACKing in MessageIn.
                                // Drop REQ and let status read as B8 to avoid permanent 0xF8 spin.
                                Signals[(int)ScsiSignal.Req] = false;
                                ret = ScsiStatus();
                                if (TraceVerboseEnabled())
                                    Console.WriteLine("CD-ROM: MessageIn watchdog deasserted REQ at PC=0xE9DF");
                            }
                            else if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_MSGIN_E9DF_FORCE_BUSFREE") == "1")
                            {
                                // Optional second-stage watchdog: if BIOS still spins on B8, force
                                // MessageIn completion to BusFree.
                                ActiveIrqs &= unchecked((byte)~(byte)CdRomIrqSource.DataTransferDone);
                                SetPhase(ScsiPhase.BusFree);
                                dataBuffer?.Dispose();
                                dataBuffer = null;
                                dataOffset = 0;
                                ret = ScsiStatus();
                                if (TraceVerboseEnabled())
                                    Console.WriteLine("CD-ROM: MessageIn watchdog forced BusFree at PC=0xE9DF");
                            }
                            _msgInE9dfPolls = 0;
                        }
                    }
                    else if (_ScsiPhase != ScsiPhase.MessageIn || HuC6280.CurrentPC != 0xE9DF)
                    {
                        _msgInE9dfPolls = 0;
                    }
                    if (!_eb5eBusFreeDumped &&
                        _ScsiPhase == ScsiPhase.BusFree &&
                        HuC6280.CurrentPC == 0xEB5E)
                    {
                        _eb5eBusFreeDumped = true;
                        Console.WriteLine("CD-ROM: EB5E first BusFree poll snapshot");
                        DumpCdRegRing();
                    }
                    MaybeLogStatusStall(ret);
                    // ACK is host-driven via IRQCTRL writes (reg 0x02).
                    // Do not synthesize ACK transitions on status polls.
                    break;

                case 0x01:
                    // Return the current SCSI data latch value.
                    // It should not advance the transfer pointer.
                    if (dataBuffer != null && dataBuffer.Length > 0)
                    {
                        long oldPos = dataBuffer.Position;
                        int peek = dataBuffer.ReadByte();
                        dataBuffer.Position = oldPos;
                        ret = peek >= 0 ? (byte)peek : (byte)0x00;
                    }
                    else
                    {
                        ret = 0x00;
                    }
                    break;

                case 0x02:
                    ret = (byte)(EnabledIrqs | (Signals[(int)ScsiSignal.Ack] ? 0x80 : 0));
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SCSI_LOG") == "1")
                        Console.WriteLine($"CD-ROM: IRQCTRL read value=0x{ret:X2} enabled=0x{EnabledIrqs:X2} ack={(Signals[(int)ScsiSignal.Ack] ? 1 : 0)}");
                    break;

                case 0x03:
                    // Compatibility/CD hardware behavior:
                    // - reading locks BRAM access
                    // - bit 4 is always set
                    // - bit 1 reflects the latched CD-DA sample side rather than mutating IRQ bits
                    bramLocked = true;
                    ret = (byte)(ActiveIrqs | 0x10 | (_cdAudioSampleToggle ? 0x00 : 0x02));
                    break;

                case 0x04:
                    ret = ResetRegValue;
                    break;

                case 0x05:
                    ret = (byte)(_cdAudioSample & 0xFF);
                    break;

                case 0x06:
                    ret = (byte)((_cdAudioSample >> 8) & 0xFF);
                    break;

                case 0x07:
                    // Reading 0x1807 clears the Sub-Q ready latch.
                    // clear Sub-Q ready latch and return 0.
                    ActiveIrqs = (byte)(ActiveIrqs & ~0x10);
                    ret = 0x00;
                    break;

                case 0x08:
                    // 0x1808 acts as the SCSI data port only in data phase.
                    // Outside data phase it returns 0.
                    if (_ScsiPhase == ScsiPhase.DataIn)
                        ret = ReadDataPort();
                    else
                        ret = 0x00;
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
            RecordCdRegAccess(isWrite: false, reg: address & 0xFF, val: ret);
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CDREG_LOG") == "1" && _cdRegLogCount < GetCdRegLogLimit())
            {
                _cdRegLogCount++;
                Console.WriteLine($"CD-ROM: RD reg=0x{(address & 0xFF):X2} val=0x{ret:X2} PC=0x{HuC6280.CurrentPC:X4}");
            }
            //Console.WriteLine("CD-ROM READ  ACCESS [ 0x{0:X} << 0x{1:X2} ]  |  CPU <0x{2:X}>", address, ret, HuC6280.CurrentPC);
            return ret;
        }

        public void WriteAt(int address, byte value)
        {
            if (Bus?.ArcadeCard != null && address >= 0x1A00 && address <= 0x1AFF)
            {
                Bus.ArcadeCard.WriteHardware(address, value);
                return;
            }

            //Console.WriteLine($"CD-ROM WRITE ACCESS [ 0x{address:X} >> 0x{value:X2} ]");
            RecordCdRegAccess(isWrite: true, reg: address & 0xFF, val: value);
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_CDREG_LOG") == "1" && _cdRegLogCount < GetCdRegLogLimit())
            {
                _cdRegLogCount++;
                Console.WriteLine($"CD-ROM: WR reg=0x{(address & 0xFF):X2} val=0x{value:X2} PC=0x{HuC6280.CurrentPC:X4}");
            }
            switch (address & 0xF)
            {
                case 0x00: // 状态/控制寄存器 处理硬件复位或其他控制信号
                    // 0x1800 command/control behavior:
                    // - 0x60 forces bus free (drops SCSI signal state)
                    // - 0x81 from bus free enters command phase
                    if (value == 0x60)
                    {
                        SetPhase(ScsiPhase.BusFree);
                        break;
                    }

                    if (value == 0x81 && _ScsiPhase == ScsiPhase.BusFree)
                    {
                        CMDBufferIndex = 0;
                        SetPhase(ScsiPhase.CMD);
                    }
                    break;

                case 0x01: // 数据端口
                    WriteDataPort(value);
                    break;

                case 0x02:
                    bool oldAck = Signals[(int)ScsiSignal.Ack];
                    bool newAck = (value & 0x80) != 0;
                    // Store the full low 7-bit IRQ mask on every write.
                    EnabledIrqs = (byte)(value & 0x7F);
                    Signals[(int)ScsiSignal.Ack] = newAck;
                    bool ackRisingEdge = !oldAck && newAck;
                    bool ackFallingEdge = oldAck && !newAck;
                    if (ackRisingEdge)
                        ProcessACK();
                    else if (ackFallingEdge &&
                             _ScsiPhase == ScsiPhase.MessageIn &&
                             Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_MSGIN_ACK_FALLING") == "1")
                        ProcessACK();
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
                        ResetController();
                    }
                    break;

                case 0x05:
                    // Latch current CD-DA sample side for 0x03/0x05/0x06 reads.
                    _cdAudioSampleToggle = !_cdAudioSampleToggle;
                    _cdAudioSample = 0;
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
            _scsiDataLatch = 0;
            dataBuffer?.Dispose();
            dataBuffer = null;
            // Hardware reset must drop host ACK; preserving ACK across phase changes is only
            // correct during normal command flow, not controller reset.
            Signals[(int)ScsiSignal.Ack] = false;
            _cdAudioSampleToggle = false;
            _cdAudioSample = 0;
            SetPhase(ScsiPhase.BusFree);
            currentSector = -1;
            //Console.WriteLine("CD-ROM Controller Reset");
        }

        private void ProcessACK()
        {
            // ACK pulse advances the current SCSI phase state machine.
            switch (_ScsiPhase)
            {
                case ScsiPhase.BusFree:
                    Signals[(int)ScsiSignal.Req] = false;
                    break;

                case ScsiPhase.CMD:
                    // The command byte is latched by the 0x1801 write and consumed on ACK.
                    CMDBuffer[CMDBufferIndex++] = _scsiDataLatch;
                    if (CMDBufferIndex == 1)
                        CMDLength = ScsiCMDLength((ScsiCommand)_scsiDataLatch);
                    if (CMDBufferIndex >= CMDLength)
                    {
                        ProcessCommand();
                        CMDBufferIndex = 0;
                    }
                    else
                    {
                        Signals[(int)ScsiSignal.Req] = true;
                    }
                    break;

                case ScsiPhase.DataIn:
                    // ACK clocks SCSI data while in DataIn.
                    // Allow opt-out for experiments.
                    if (Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_ACK_CLOCKS_DATA") != "0")
                        _ = ReadDataPort();
                    break;

                case ScsiPhase.Status:
                    FinishCommand();
                    break;

                case ScsiPhase.MessageIn:
                    // Clear DataTransferDone and drop to BusFree on ACK.
                    ActiveIrqs &= unchecked((byte)~(byte)CdRomIrqSource.DataTransferDone);
                    SetPhase(ScsiPhase.BusFree);
                    break;

                case ScsiPhase.Busy:
                    break;
            }

            UpdateScsiIrqs();
        }

        private byte ScsiStatus()
        {
            byte status = 0;
            bool reqVisible = Signals[(int)ScsiSignal.Req] && !Signals[(int)ScsiSignal.Ack];
            if (Signals[(int)ScsiSignal.Io]) status |= 0x08;
            if (Signals[(int)ScsiSignal.Cd]) status |= 0x10;
            if (Signals[(int)ScsiSignal.Msg]) status |= 0x20;
            if (reqVisible) status |= 0x40;
            if (Signals[(int)ScsiSignal.Bsy]) status |= 0x80;
            return status;
        }
        #endregion
    }
}
