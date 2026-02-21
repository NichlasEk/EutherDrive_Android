using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EutherDrive.Core;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdCddStub
{
    private const int Divider75Hz = 44100 / 75;
    private const int PlayDelayClocks = 6;
    private const int FastForwardSeconds = 1;
    private const int FastForwardFrames = 25;
    private const int BytesPerAudioSample = 4;
    private const int MaxFaderVolume = 1 << 10;

    private enum DriveStatus : byte
    {
        Stopped = 0x00,
        Playing = 0x01,
        Seeking = 0x02,
        Scanning = 0x03,
        Paused = 0x04,
        TrayOpen = 0x05,
        InvalidCommand = 0x07,
        ReadingToc = 0x09,
        TrackSkipping = 0x0A,
        NoDisc = 0x0B,
        DiscEnd = 0x0C,
        DiscStart = 0x0D,
        TrayMoving = 0x0E
    }

    public enum CdModel
    {
        One,
        Two
    }

    private enum ReportType : byte
    {
        AbsoluteTime = 0x00,
        RelativeTime = 0x01,
        CurrentTrack = 0x02,
        DiscLength = 0x03,
        StartAndEndTracks = 0x04,
        TrackNStartTime = 0x05
    }

    private enum ReaderStatus
    {
        Playing,
        Paused
    }

    private enum State
    {
        MotorStopped,
        NoDisc,
        PreparingToPlay,
        Playing,
        Paused,
        Seeking,
        TrackSkipping,
        FastForwarding,
        Rewinding,
        DiscStart,
        DiscEnd,
        TrayOpening,
        TrayOpen,
        TrayClosing,
        InvalidCommand,
        ReadingToc
    }

    private State _state = State.MotorStopped;
    private ReportType _reportType = ReportType.AbsoluteTime;
    private int _reportTrackNumber;
    private CdTime _stateTime = CdTime.Zero;
    private CdTime _seekCurrent = CdTime.Zero;
    private CdTime _seekTarget = CdTime.Zero;
    private int _seekClocks;
    private ReaderStatus _seekNextStatus;
    private CdTime _skipCurrent = CdTime.Zero;
    private CdTime _skipTarget = CdTime.Zero;
    private int _skipClocks;
    private bool _trayAutoClose;
    private CdTime? _nextClockPlay;
    private bool _interruptPending;
    private readonly byte[] _status = new byte[10];
    private readonly byte[] _sectorBuffer = new byte[CdRom.BytesPerSector];
    private CdRom? _disc;
    private CdModel _model = CdModel.One;
    private int _divider75Hz = Divider75Hz;
    private ushort _faderVolume;
    private ushort _currentVolume;
    private ushort _audioSampleIdx;
    private bool _loadedAudioSector;
    private State _lastStatusState;
    private int _dataSpeed = 1;
    private double _lastAudioLeft;
    private double _lastAudioRight;

    private static readonly double[] FaderVolumeTable = BuildFaderTable();

    private static readonly bool LogCdd = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDD"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogCddIrq = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDD_IRQ"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceCddTimeline = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CDD_TIMELINE"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceCddState = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CDD_STATE"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceCddSeek = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CDD_SEEK"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool MotorStoppedToReadingToc = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_CDD_MOTORSTOP_TOC"),
        "1",
        StringComparison.Ordinal);
    private static readonly long TraceStartTicks = Stopwatch.GetTimestamp();

    public byte[] Status => _status;
    public bool InterruptPending => _interruptPending;

    public bool PlayingAudio => IsPlayingAudio();
    public (double Left, double Right) LastAudioSample => (_lastAudioLeft, _lastAudioRight);

    public void SetModel(CdModel model)
    {
        _model = model;
    }

    public void SetFaderVolume(ushort volume)
    {
        _faderVolume = volume > MaxFaderVolume ? (ushort)MaxFaderVolume : volume;
    }

    internal void SetDisc(CdRom? disc)
    {
        ChangeDisc(disc);
    }

    internal void RemoveDisc()
    {
        _disc = null;
        _state = State.TrayOpening;
        _trayAutoClose = _model == CdModel.Two;
        _stateTime = CdTime.Zero;
        _nextClockPlay = null;
        _audioSampleIdx = 0;
        _loadedAudioSector = false;
        UpdateStatus();
        _lastStatusState = _state;
        _lastStatusState = _state;
    }

    internal void ChangeDisc(CdRom? disc)
    {
        if (disc == null)
        {
            RemoveDisc();
            return;
        }

        _disc = disc;
        if (_model == CdModel.Two)
        {
            _state = State.TrayOpening;
            _trayAutoClose = true;
        }
        else if (_state == State.NoDisc)
        {
            _state = State.MotorStopped;
        }

        _stateTime = CdTime.Zero;
        _reportType = ReportType.AbsoluteTime;
        _reportTrackNumber = 0;
        _nextClockPlay = null;
        _audioSampleIdx = 0;
        _loadedAudioSector = false;
        UpdateStatus();
        _lastStatusState = _state;
    }

    public void SendCommand(byte[] command)
    {
        if (command == null || command.Length < 10)
            return;

        if (TraceCddTimeline)
        {
            Console.Error.WriteLine(
                $"[SCD-TL CDD] t={TraceStamp()} cmd={string.Join(" ", command)} state={_state}");
        }
        if (LogCdd)
            Console.WriteLine($"[SCD-CDD] CMD: {string.Join(" ", command)}");

        CdTime? prevPlayingTime = _state == State.Playing ? _stateTime : null;

        switch (command[0])
        {
            case 0x00:
                break;
            case 0x01:
                _state = State.MotorStopped;
                _reportType = ReportType.AbsoluteTime;
                break;
            case 0x02:
                ExecuteReadToc(command);
                break;
            case 0x03:
                ExecuteSeek(command, ReaderStatus.Playing);
                break;
            case 0x04:
                ExecuteSeek(command, ReaderStatus.Paused);
                break;
            case 0x06:
                _state = _disc == null ? State.NoDisc : State.Paused;
                _stateTime = CurrentTime();
                break;
            case 0x07:
                if (_state is State.Paused or State.FastForwarding or State.Rewinding)
                {
                    _state = State.PreparingToPlay;
                    _stateTime = CurrentTime();
                    _seekClocks = PlayDelayClocks;
                }
                break;
            case 0x08:
                if (_disc != null)
                {
                    _state = State.FastForwarding;
                    _stateTime = CurrentTime();
                }
                break;
            case 0x09:
                if (_disc != null)
                {
                    _state = State.Rewinding;
                    _stateTime = CurrentTime();
                }
                break;
            case 0x0A:
                ExecuteTrackSkip(command);
                break;
            case 0x0C:
                if (_state is State.TrayOpening or State.TrayOpen)
                    _state = State.TrayClosing;
                break;
            case 0x0D:
                if (_state != State.TrayOpening)
                    _state = State.TrayOpening;
                _trayAutoClose = false;
                break;
        }

        if (command[0] != 0x00 && command[0] != 0x02 && _reportType == ReportType.TrackNStartTime)
        {
            _reportType = ReportType.AbsoluteTime;
            _reportTrackNumber = 0;
        }

        UpdateStatus();

        if (TraceCddTimeline)
        {
            Console.Error.WriteLine(
                $"[SCD-TL CDD] t={TraceStamp()} status={string.Join(" ", _status)} state={_state}");
        }

        if (prevPlayingTime.HasValue && _state != State.Playing)
            _nextClockPlay = prevPlayingTime.Value;
    }

    public void Reset()
    {
        _interruptPending = false;
        _state = State.MotorStopped;
        _stateTime = CdTime.Zero;
        _reportType = ReportType.AbsoluteTime;
        _reportTrackNumber = 0;
        _nextClockPlay = null;
        _divider75Hz = Divider75Hz;
        _currentVolume = 0;
        _audioSampleIdx = 0;
        _loadedAudioSector = false;
        UpdateStatus();
        _lastStatusState = _state;
    }

    public string? DiscTitle(ConsoleRegion region)
    {
        if (_disc == null)
            return null;

        if (!_disc.ReadSector(new CdTime(0, 2, 0), _sectorBuffer))
            return null;
        int start = region == ConsoleRegion.JP ? 0x130 : 0x160;
        int end = start + 0x30;
        var builder = new StringBuilder(0x30);
        for (int i = start; i < end; i++)
        {
            char c = (char)_sectorBuffer[i];
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c))
                builder.Append(c);
        }

        string raw = builder.ToString().Trim();
        if (raw.Length == 0)
            return null;

        return CollapseWhitespace(raw);
    }

    public void Tick(bool hostClockOn)
    {
        // No-op; CDD interrupts are generated on the 75Hz clock.
    }

    public void AcknowledgeInterrupt()
    {
        _interruptPending = false;
    }

    public void Clock44100Hz(SegaCdCdcStub cdc, WordRam wordRam, byte[] prgRam, bool prgRamAccessible, SegaCdPcmStub pcm)
    {
        AdjustFaderVolume();
        SampleAudio();

        int dividerDecrement = ReadingDataTrack() ? _dataSpeed : 1;
        for (int i = 0; i < dividerDecrement; i++)
        {
            _divider75Hz--;
            if (_divider75Hz <= 0)
            {
                _divider75Hz = Divider75Hz;
                Clock75Hz(cdc);
                cdc.Clock75Hz();
            }
            else
            {
                cdc.Clock44100Hz(wordRam, prgRam, prgRamAccessible, pcm);
            }
        }
    }

    private void Clock75Hz(SegaCdCdcStub cdc)
    {
        State prevState = _state;
        _interruptPending = true;
        if (LogCddIrq)
            Console.WriteLine($"[SCD-CDD-IRQ] pending=1 state={_state} time={CurrentTime()}");
        if (TraceCddState)
            Console.Error.WriteLine($"[SCD-CDD-STATE] t={TraceStamp()} state={_state} time={CurrentTime()}");
        if (TraceCddSeek && _state == State.Seeking)
        {
            Console.Error.WriteLine(
                $"[SCD-CDD-SEEK] t={TraceStamp()} cur={_seekCurrent} target={_seekTarget} clocks={_seekClocks} next={_seekNextStatus}");
        }

        if (_audioSampleIdx != 0)
            _audioSampleIdx = 0;

        if (_nextClockPlay.HasValue)
        {
            HandlePlaying(_nextClockPlay.Value, changeState: false, cdc);
            _nextClockPlay = null;
            return;
        }

        switch (_state)
        {
            case State.Seeking:
                if (_seekClocks <= 1)
                {
                    _stateTime = _seekTarget;
                    if (_seekNextStatus == ReaderStatus.Paused)
                    {
                        _state = State.Paused;
                    }
                    else
                    {
                        // BIOS can get stuck waiting for play; enter Playing immediately and
                        // prime the CDC with the first sector.
                        _state = State.Playing;
                        _loadedAudioSector = false;
                        HandlePlaying(_stateTime, changeState: true, cdc);
                    }
                }
                else
                {
                    _seekClocks--;
                    _seekCurrent = EstimateIntermediateSeekTime(_seekCurrent, _seekTarget, _seekClocks);
                }
                break;
            case State.TrackSkipping:
                if (_skipClocks <= 1)
                {
                    _stateTime = _skipTarget;
                    _state = State.Paused;
                }
                else
                {
                    _skipClocks--;
                    _skipCurrent = EstimateIntermediateSeekTime(_skipCurrent, _skipTarget, _skipClocks);
                }
                break;
            case State.FastForwarding:
                {
                    CdTime time = CurrentTime();
                    CdTime newTime = time.AddFrames(FastForwardSeconds * 75 + FastForwardFrames);
                    CdTime discEnd = GetDiscEndTime();
                    _stateTime = newTime >= discEnd ? discEnd : newTime;
                    if (newTime >= discEnd)
                        _state = State.DiscEnd;
                    break;
                }
            case State.Rewinding:
                {
                    CdTime time = CurrentTime();
                    int newFrames = Math.Max(0, time.ToFrames() - (FastForwardSeconds * 75 + FastForwardFrames));
                    _stateTime = CdTime.FromFrames(newFrames);
                    if (_stateTime.ToFrames() == 0)
                        _state = State.DiscStart;
                    break;
                }
            case State.PreparingToPlay:
                if (_seekClocks <= 1)
                {
                    _state = State.Playing;
                    _loadedAudioSector = false;
                    if (TraceCddState)
                        Console.Error.WriteLine($"[SCD-CDD-STATE] t={TraceStamp()} -> Playing time={_stateTime}");
                    // Prime CDC with the first sector immediately so header data is available.
                    HandlePlaying(_stateTime, changeState: true, cdc);
                }
                else
                {
                    _seekClocks--;
                }
                break;
            case State.Playing:
                if (TraceCddState)
                    Console.Error.WriteLine($"[SCD-CDD-STATE] t={TraceStamp()} Playing time={_stateTime}");
                HandlePlaying(_stateTime, changeState: true, cdc);
                break;
            case State.MotorStopped:
                if (_disc == null)
                {
                    _state = State.NoDisc;
                }
                else if (MotorStoppedToReadingToc)
                {
                    // Match jgenesis: transition to ReadingToc one clock after motor stop
                    _state = State.ReadingToc;
                }
                break;
            case State.TrayOpening:
                _state = State.TrayOpen;
                break;
            case State.TrayClosing:
                _state = State.MotorStopped;
                _reportType = ReportType.AbsoluteTime;
                _reportTrackNumber = 0;
                break;
            case State.TrayOpen:
                if (_trayAutoClose)
                    _state = State.TrayClosing;
                break;
        }
        if (_state != prevState && _state != _lastStatusState)
        {
            UpdateStatus();
            _lastStatusState = _state;
        }
    }

    private void HandlePlaying(CdTime time, bool changeState, SegaCdCdcStub cdc)
    {
        if (_disc == null)
        {
            if (changeState)
                _state = State.NoDisc;
            return;
        }

        CdTrack? track = _disc.Cue.FindTrackByTime(time);
        if (track == null)
        {
            if (changeState)
            {
                _state = State.DiscEnd;
                _stateTime = _disc.Cue.LastTrack.EndTime;
            }
            return;
        }

        // Read by track-relative time (matches jgenesis timing model).
        CdTime relative = time.SaturatingSub(track.StartTime);
        _disc.ReadSector(track.Number, relative, _sectorBuffer);
        cdc.DecodeBlock(_sectorBuffer);
        _loadedAudioSector = track.TrackType == CdTrackType.Audio;

        if (changeState)
        {
            _stateTime = time.AddFrames(1);
            _state = State.Playing;
        }
    }

    private CdTime CurrentTime()
    {
        return _state switch
        {
            State.MotorStopped or State.NoDisc or State.DiscStart or State.ReadingToc or State.TrayOpening or State.TrayOpen or State.TrayClosing => CdTime.Zero,
            State.PreparingToPlay or State.Playing or State.Paused or State.FastForwarding or State.Rewinding or State.DiscEnd or State.InvalidCommand => _stateTime,
            State.Seeking => _seekCurrent,
            State.TrackSkipping => _skipCurrent,
            _ => _stateTime
        };
    }

    private void UpdateStatus()
    {
        Array.Clear(_status, 0, _status.Length);
        _status[0] = (byte)CurrentStatus();

        if (_state == State.MotorStopped)
        {
            UpdateChecksum();
            return;
        }

        if (_state is State.Seeking or State.TrackSkipping or State.TrayOpening or State.TrayOpen or State.TrayClosing or State.NoDisc)
        {
            _status[1] = 0x0F;
            UpdateChecksum();
            return;
        }

        byte report = (byte)_reportType;
        if (report > 0x05)
            report = 0x00;
        _status[1] = report;

        if (_disc != null)
        {
            switch (_reportType)
            {
                case ReportType.AbsoluteTime:
                    WriteTimeToStatus(CurrentTime());
                    _status[8] = StatusFlags();
                    break;
                case ReportType.RelativeTime:
                    {
                        CdTime current = CurrentTime();
                        CdTime trackStart = _disc.Cue.FindTrackByTime(current)?.EffectiveStartTime() ?? CdTime.Zero;
                        WriteTimeToStatus(current.SaturatingSub(trackStart));
                        _status[8] = StatusFlags();
                        break;
                    }
                case ReportType.CurrentTrack:
                    {
                        CdTime current = CurrentTime();
                        int num = _disc.Cue.FindTrackByTime(current)?.Number ?? 0;
                        _status[2] = (byte)(num / 10);
                        _status[3] = (byte)(num % 10);
                        _status[8] = StatusFlags();
                        break;
                    }
                case ReportType.DiscLength:
                    WriteTimeToStatus(_disc.Cue.LastTrack.EndTime);
                    _status[8] = StatusFlags();
                    break;
                case ReportType.StartAndEndTracks:
                    _status[2] = 0x00;
                    _status[3] = 0x01;
                    int endTrack = _disc.Cue.LastTrack.Number;
                    _status[4] = (byte)(endTrack / 10);
                    _status[5] = (byte)(endTrack % 10);
                    _status[8] = StatusFlags();
                    break;
                case ReportType.TrackNStartTime:
                    {
                        int trackNum = _reportTrackNumber;
                        CdTrack track = trackNum <= _disc.Cue.LastTrack.Number ? _disc.Cue.Track(trackNum) : _disc.Cue.LastTrack;
                        WriteTimeToStatus(track.EffectiveStartTime());
                        if (track.TrackType == CdTrackType.Data)
                            _status[6] |= 0x08;
                        _status[8] = (byte)(trackNum % 10);
                        break;
                    }
            }
        }

        UpdateChecksum();
    }

    private DriveStatus CurrentStatus()
    {
        return _state switch
        {
            State.MotorStopped => DriveStatus.Stopped,
            State.NoDisc => DriveStatus.NoDisc,
            State.Paused => DriveStatus.Paused,
            State.PreparingToPlay or State.Playing => DriveStatus.Playing,
            State.Seeking => DriveStatus.Seeking,
            State.TrackSkipping => DriveStatus.TrackSkipping,
            State.FastForwarding or State.Rewinding => DriveStatus.Scanning,
            State.DiscStart => DriveStatus.DiscStart,
            State.DiscEnd => DriveStatus.DiscEnd,
            State.TrayOpening or State.TrayClosing => DriveStatus.TrayMoving,
            State.TrayOpen => DriveStatus.TrayOpen,
            State.InvalidCommand => DriveStatus.InvalidCommand,
            State.ReadingToc => DriveStatus.ReadingToc,
            _ => DriveStatus.Stopped
        };
    }

    private byte StatusFlags()
    {
        bool playingData = _state == State.Playing || _state == State.PreparingToPlay;
        if (playingData && _disc != null)
        {
            CdTime time = CurrentTime();
            CdTrack? track = _disc.Cue.FindTrackByTime(time);
            if (track != null && track.TrackType == CdTrackType.Data)
                return 0x04;
        }
        return 0x00;
    }

    private void WriteTimeToStatus(CdTime time)
    {
        _status[2] = (byte)(time.Minutes / 10);
        _status[3] = (byte)(time.Minutes % 10);
        _status[4] = (byte)(time.Seconds / 10);
        _status[5] = (byte)(time.Seconds % 10);
        _status[6] = (byte)(time.Frames / 10);
        _status[7] = (byte)(time.Frames % 10);
    }

    private void UpdateChecksum()
    {
        byte sum = 0;
        for (int i = 0; i < 9; i++)
            sum += _status[i];
        _status[9] = (byte)((~sum) & 0x0F);
    }

    private void ExecuteReadToc(byte[] command)
    {
        _reportType = ReportTypeFromCommand(command, out int trackNumber);
        _reportTrackNumber = trackNumber;

        State nextState = (_state, _disc) switch
        {
            (State.MotorStopped, null) => State.NoDisc,
            (State.MotorStopped, _) => State.Paused,
            (_, _) when _reportType == ReportType.TrackNStartTime => State.ReadingToc,
            _ => _state
        };

        if (_state != nextState)
        {
            _state = nextState;
            if (_state == State.Paused)
                _stateTime = CdTime.Zero;
        }
    }

    private static ReportType ReportTypeFromCommand(byte[] command, out int trackNumber)
    {
        trackNumber = 0;
        byte report = (byte)(command[3] & 0x0F);
        return report switch
        {
            0x00 => ReportType.AbsoluteTime,
            0x01 => ReportType.RelativeTime,
            0x02 => ReportType.CurrentTrack,
            0x03 => ReportType.DiscLength,
            0x04 => ReportType.StartAndEndTracks,
            0x05 => TrackNStart(command, out trackNumber),
            _ => ReportType.AbsoluteTime
        };
    }

    private static ReportType TrackNStart(byte[] command, out int trackNumber)
    {
        trackNumber = 10 * (command[4] & 0x0F) + (command[5] & 0x0F);
        return ReportType.TrackNStartTime;
    }

    private void ExecuteSeek(byte[] command, ReaderStatus nextStatus)
    {
        if (_disc == null)
        {
            _state = State.NoDisc;
            return;
        }

        if (!TryReadTimeFromCommand(command, out CdTime seekTime))
        {
            _state = State.InvalidCommand;
            _stateTime = CurrentTime();
            return;
        }

        CdTime current = CurrentTime();
        if (seekTime.ToFrames() == current.ToFrames())
        {
            _state = nextStatus == ReaderStatus.Paused ? State.Paused : State.PreparingToPlay;
            _stateTime = seekTime;
            if (nextStatus == ReaderStatus.Playing)
                _seekClocks = PlayDelayClocks;
            return;
        }

        int seekClocks = Math.Max(7, EstimateSeekClocks(current, seekTime));
        _seekCurrent = current;
        _seekTarget = seekTime;
        _seekNextStatus = nextStatus;
        _seekClocks = seekClocks;
        _state = State.Seeking;
    }

    private void ExecuteTrackSkip(byte[] command)
    {
        if (_disc == null)
        {
            _state = State.NoDisc;
            return;
        }

        uint skipTracks = (uint)((command[4] & 0x0F) << 12)
            | (uint)((command[5] & 0x0F) << 8)
            | (uint)((command[6] & 0x0F) << 4)
            | (uint)(command[7] & 0x0F);

        uint skipBlocks = 15 * skipTracks;
        CdTime current = CurrentTime();
        int currentSector = current.ToFrames();
        int skipSector;
        if ((command[3] & 0x0F) == 0)
        {
            int discEnd = GetDiscEndTime().ToFrames();
            skipSector = Math.Min(discEnd, currentSector + (int)skipBlocks);
        }
        else
        {
            skipSector = Math.Max(0, currentSector - (int)skipBlocks);
        }

        CdTime skipTime = CdTime.FromFrames(skipSector);
        _skipCurrent = current;
        _skipTarget = skipTime;
        _skipClocks = EstimateSeekClocks(current, skipTime);
        _state = State.TrackSkipping;
    }

    private bool IsPlayingAudio()
    {
        if (_state != State.Playing || _disc == null)
            return false;
        CdTrack? track = _disc.Cue.FindTrackByTime(_stateTime);
        return track != null && track.TrackType == CdTrackType.Audio && _loadedAudioSector;
    }

    private bool ReadingDataTrack()
    {
        if (_state != State.Playing || _disc == null)
            return false;
        CdTrack? track = _disc.Cue.FindTrackByTime(_stateTime);
        return track != null && track.TrackType == CdTrackType.Data;
    }

    private void AdjustFaderVolume()
    {
        if (_currentVolume < _faderVolume)
            _currentVolume++;
        else if (_currentVolume > _faderVolume)
            _currentVolume--;
    }

    private void SampleAudio()
    {
        if (!IsPlayingAudio())
        {
            _audioSampleIdx = 0;
            _lastAudioLeft = 0.0;
            _lastAudioRight = 0.0;
            return;
        }

        int idx = _audioSampleIdx;
        short sampleL = (short)(_sectorBuffer[idx] | (_sectorBuffer[idx + 1] << 8));
        short sampleR = (short)(_sectorBuffer[idx + 2] | (_sectorBuffer[idx + 3] << 8));

        double multiplier = FaderVolumeTable[_currentVolume];
        _lastAudioLeft = multiplier * (sampleL / -(double)short.MinValue);
        _lastAudioRight = multiplier * (sampleR / -(double)short.MinValue);

        _audioSampleIdx = (ushort)((_audioSampleIdx + BytesPerAudioSample) % CdRom.BytesPerSector);
    }

    private static double[] BuildFaderTable()
    {
        double[] table = new double[MaxFaderVolume + 1];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = i switch
            {
                0 => 0.0,
                1 or 2 or 3 => i / 1024.0,
                _ => (i >> 2) / 256.0
            };
        }
        return table;
    }

    private static string CollapseWhitespace(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool inWhitespace = false;
        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                inWhitespace = false;
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    private static string TraceStamp()
    {
        long ticks = Stopwatch.GetTimestamp() - TraceStartTicks;
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        return ms.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static bool TryReadTimeFromCommand(byte[] command, out CdTime time)
    {
        int minutes = 10 * (command[2] & 0x0F) + (command[3] & 0x0F);
        int seconds = 10 * (command[4] & 0x0F) + (command[5] & 0x0F);
        int frames = 10 * (command[6] & 0x0F) + (command[7] & 0x0F);
        if (seconds >= 60 || frames >= 75)
        {
            time = CdTime.Zero;
            return false;
        }
        time = new CdTime(minutes, seconds, frames);
        return true;
    }

    private int EstimateSeekClocks(CdTime current, CdTime target)
    {
        int diffFrames = Math.Abs(current.ToFrames() - target.ToFrames());
        int discEndFrames = Math.Max(1, GetDiscEndTime().ToFrames());
        int seekCycles = (int)Math.Round(113.0 * diffFrames / discEndFrames);
        return Math.Max(1, seekCycles);
    }

    private CdTime EstimateIntermediateSeekTime(CdTime current, CdTime target, int clocksRemaining)
    {
        int discEndFrames = Math.Max(1, GetDiscEndTime().ToFrames());
        int diffFrames = (int)Math.Round((double)clocksRemaining / 113.0 * discEndFrames);
        if (current.ToFrames() < target.ToFrames())
            return CdTime.FromFrames(Math.Max(0, target.ToFrames() - diffFrames));
        return CdTime.FromFrames(target.ToFrames() + diffFrames);
    }

    private CdTime GetDiscEndTime()
    {
        return _disc?.Cue.LastTrack.EndTime ?? new CdTime(99, 59, 74);
    }
}
