namespace EutherDrive.Core.Arcade.Technos;

public sealed class XainSleenaAdapter : IEmulatorCore, IDisposable
{
    private readonly McsArcadeAdapter _adapter = new();

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "xsleena" or "xsleenaj" or "solrwarr" or "xsleenab";
    }

    public void LoadRom(string path) => _adapter.LoadRom(path);
    public void Reset() => _adapter.Reset();
    public void RunFrame() => _adapter.RunFrame();

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
