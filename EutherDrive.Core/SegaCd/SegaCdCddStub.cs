namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdCddStub
{
    private readonly byte[] _status = new byte[10];
    private bool _interruptPending;
    private bool _playingAudio;
    private ushort _faderVolume;
    private bool _discPresent;
    private Status _state = Status.NoDisc;
    private ReportType _reportType = ReportType.AbsoluteTime;

    public byte[] Status => _status;

    public bool InterruptPending => _interruptPending;

    public void AcknowledgeInterrupt()
    {
        _interruptPending = false;
    }

    public bool PlayingAudio => _playingAudio;

    public void SetFaderVolume(ushort volume)
    {
        _faderVolume = volume;
    }

    public void SendCommand(byte[] command)
    {
        if (command == null || command.Length < 1)
            return;

        byte op = command[0];
        switch (op)
        {
            case 0x00: // NOP
                break;
            case 0x01: // Stop motor
                _state = Status.Stopped;
                break;
            case 0x02: // Read TOC
                _reportType = ReportTypeExtensions.FromCommand(command);
                _state = _discPresent ? Status.Paused : Status.NoDisc;
                break;
            case 0x03: // Seek and play
            case 0x07: // Play
                _state = _discPresent ? Status.Playing : Status.NoDisc;
                break;
            case 0x04: // Seek
                _state = _discPresent ? Status.Seeking : Status.NoDisc;
                break;
            case 0x06: // Pause
                _state = _discPresent ? Status.Paused : Status.NoDisc;
                break;
            case 0x0D: // Open tray
                _state = Status.TrayOpen;
                break;
            case 0x0C: // Close tray
                _state = _discPresent ? Status.Paused : Status.NoDisc;
                break;
            default:
                _state = Status.InvalidCommand;
                break;
        }

        UpdateStatus();
        _interruptPending = true;
    }

    public void Reset()
    {
        _interruptPending = false;
        _playingAudio = false;
        _faderVolume = 0;
        _state = _discPresent ? Status.Paused : Status.NoDisc;
        UpdateStatus();
    }

    public void SetDiscPresent(bool present)
    {
        _discPresent = present;
        if (!_discPresent && _state != Status.TrayOpen)
            _state = Status.NoDisc;
        else if (_discPresent && _state == Status.NoDisc)
            _state = Status.Paused;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        for (int i = 0; i < _status.Length; i++)
            _status[i] = 0;
        _status[0] = (byte)_state;
        _status[1] = ReportTypeExtensions.ToByte(_reportType);
        UpdateChecksum();
    }

    private void UpdateChecksum()
    {
        byte sum = 0;
        for (int i = 0; i < 9; i++)
            sum += _status[i];
        _status[9] = (byte)((~sum) & 0x0F);
    }

    private enum Status : byte
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

    private enum ReportType : byte
    {
        AbsoluteTime = 0x00,
        RelativeTime = 0x01,
        CurrentTrack = 0x02,
        DiscLength = 0x03,
        StartAndEndTracks = 0x04
    }

    private static class ReportTypeExtensions
    {
        public static ReportType FromCommand(byte[] command)
        {
            if (command.Length < 4)
                return ReportType.AbsoluteTime;
            byte mode = command[3];
            return mode switch
            {
                0x01 => ReportType.RelativeTime,
                0x02 => ReportType.CurrentTrack,
                0x03 => ReportType.DiscLength,
                0x04 => ReportType.StartAndEndTracks,
                _ => ReportType.AbsoluteTime
            };
        }

        public static byte ToByte(this ReportType reportType)
        {
            return (byte)reportType;
        }
    }
}
