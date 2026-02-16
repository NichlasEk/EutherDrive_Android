namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdCddStub
{
    private readonly byte[] _status = new byte[10];
    private bool _interruptPending;
    private bool _playingAudio;
    private ushort _faderVolume;

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
        // TODO: parse CDD commands
        _interruptPending = true;
    }

    public void Reset()
    {
        _interruptPending = false;
        _playingAudio = false;
        _faderVolume = 0;
        for (int i = 0; i < _status.Length; i++)
            _status[i] = 0;
    }
}
