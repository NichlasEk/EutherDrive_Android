namespace EutherDrive.Core.Arcade.Toaplan;

using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

public sealed class BatsugunAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private static readonly HashSet<string> SupportedDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "batsugunsp"
    };

    private static readonly string[] RequiredSpecialSetEntries =
    {
        "tp-030sp.u69",
        "tp030_2.bin",
        "tp030_3l.bin",
        "tp030_3h.bin",
        "tp030_4l.bin",
        "tp030_4h.bin",
        "tp030_5.bin",
        "tp030_6.bin"
    };

    private readonly McsArcadeAdapter _adapter = new();

    public RomInfo RomInfo { get; } = new()
    {
        Summary = "Toaplan Batsugun adapter idle",
        RegionHint = ConsoleRegion.Auto
    };

    public RomIdentity? RomIdentity => _adapter.RomIdentity;
    public long? FrameCounter => _adapter.FrameCounter;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = GetDriverName(path);
        if (SupportedDrivers.Contains(name))
            return true;

        return LooksLikeBatsugunSpecialArchive(path);
    }

    public static bool IsSupportedDriverName(string driverName)
        => SupportedDrivers.Contains(driverName);

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Batsugun ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Batsugun ROM archive not found.", path);
        if (!IsSupportedArchive(path))
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is not recognized as a Toaplan Batsugun Special MAME set.");

        string driverName = GetDriverName(path);
        if (!SupportedDrivers.Contains(driverName))
            driverName = "batsugunsp";

        UpdateRomInfo(path, driverName);
        _adapter.LoadRom(path);
    }

    public void Reset() => _adapter.Reset();
    public void RunFrame() => _adapter.RunFrame();

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
        => _adapter.GetFrameBuffer(out width, out height, out stride);

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
        => _adapter.GetAudioBuffer(out sampleRate, out channels);

    public void SetMasterVolumePercent(int percent)
        => _adapter.SetMasterVolumePercent(percent);

    public double GetTargetFps() => 27_000_000.0 / 4.0 / (432.0 * 262.0);

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
        => _adapter.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);

    public void SaveState(BinaryWriter writer) => _adapter.SaveState(writer);
    public void LoadState(BinaryReader reader) => _adapter.LoadState(reader);

    public void Dispose() => _adapter.Dispose();

    private void UpdateRomInfo(string path, string driverName)
    {
        RomInfo.Summary = "Toaplan Batsugun - Special Version";
        RomInfo.ExtraInfo =
            $"MAME set: {driverName}\n" +
            $"Archive: {Path.GetFileName(path)}\n" +
            "Reference: ~/mame/src/mame/toaplan/batsugun.cpp\n" +
            "Hardware: Toaplan2, 68000 @ 16 MHz, V25 audio CPU, dual GP9001 video, YM2151 + OKIM6295.";
        RomInfo.RegionHint = ConsoleRegion.Auto;
    }

    private static string GetDriverName(string path)
        => Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();

    private static bool LooksLikeBatsugunSpecialArchive(string path)
    {
        try
        {
            using IArchive archive = ArchiveFactory.Open(path);
            var names = new HashSet<string>(
                archive.Entries
                    .Where(static entry => !entry.IsDirectory)
                    .Select(static entry => Path.GetFileName(entry.Key).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            return RequiredSpecialSetEntries.All(names.Contains);
        }
        catch
        {
            return false;
        }
    }
}
