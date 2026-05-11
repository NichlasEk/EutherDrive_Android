namespace EutherDrive.Core.Arcade.Igs;

using EutherDrive.Core.Savestates;

public sealed class KovPgmAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private readonly McsArcadeAdapter _adapter = new();

    public KovPgmAdapter()
    {
        _adapter.SetOutputGainPercent(1000);
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return IsSupportedDriverName(name);
    }

    public static bool IsSupportedDriverName(string driverName)
        => driverName is "kov" or "orlegend" or "dmnfrnt" or "ddpdoj" or "espgal" or "ket" or "kov2" or "theglad";

    public void LoadRom(string path) => _adapter.LoadRom(path);
    public void Reset() => _adapter.Reset();
    public void RunFrame() => _adapter.RunFrame();

    public RomIdentity? RomIdentity => _adapter.RomIdentity;
    public long? FrameCounter => _adapter.FrameCounter;

    public void SaveState(BinaryWriter writer) => _adapter.SaveState(writer);
    public void LoadState(BinaryReader reader) => _adapter.LoadState(reader);

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
        => _adapter.GetFrameBuffer(out width, out height, out stride);

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
        => _adapter.GetAudioBuffer(out sampleRate, out channels);

    public void SetMasterVolumePercent(int percent)
        => _adapter.SetMasterVolumePercent(percent);

    public void SetInputState(
        bool up, bool down, bool left, bool right,
        bool a, bool b, bool c, bool start,
        bool x, bool y, bool z, bool mode,
        PadType padType)
        => _adapter.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);

    public void Dispose() => _adapter.Dispose();
}
