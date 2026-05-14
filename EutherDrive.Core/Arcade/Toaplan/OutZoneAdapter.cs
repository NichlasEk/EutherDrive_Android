namespace EutherDrive.Core.Arcade.Toaplan;

using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

public sealed class OutZoneAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private static readonly HashSet<string> SupportedDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "outzone",
        "outzonea",
        "outzoneb",
        "outzonec",
        "outzoneh",
        "outzonecv"
    };

    private static readonly string[] RequiredMergedSetEntries =
    {
        "tp-018_rom1.1e",
        "tp-018_rom2.1c",
        "tp-018_rom3.1d",
        "tp-018_rom4.1b",
        "tp-018_rom5.19h",
        "tp-018_rom6.22h",
        "tp_018_09.3j"
    };

    private readonly McsArcadeAdapter _adapter = new();

    public RomInfo RomInfo { get; } = new()
    {
        Summary = "Toaplan Out Zone adapter idle",
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

        return LooksLikeMergedOutZoneArchive(path);
    }

    public static bool IsSupportedDriverName(string driverName)
        => SupportedDrivers.Contains(driverName);

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Out Zone ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Out Zone ROM archive not found.", path);
        if (!IsSupportedArchive(path))
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is not recognized as a Toaplan Out Zone MAME set.");

        string driverName = GetDriverName(path);
        if (!SupportedDrivers.Contains(driverName))
            driverName = "outzone";

        UpdateRomInfo(path, driverName);

        if (!McsDriverCatalog.Contains(driverName))
        {
            throw new NotSupportedException(
                $"Toaplan Out Zone set '{driverName}' was recognized, but the bundled MCS/MAME snapshot does not expose the Toaplan1 driver yet. " +
                "Bringup stage 1 is wired: ROM detection, UI/headless routing, target timing, input/audio delegation, and local MAME reference metadata are ready. " +
                "Next step is porting ~/mame/src/mame/toaplan/toaplan1.cpp plus the Toaplan FCU/BCU/video-controller devices into MCS.");
        }

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

    public double GetTargetFps() => 7_000_000.0 / (450.0 * 282.0);

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
        RomInfo.Summary = driverName.Equals("outzone", StringComparison.OrdinalIgnoreCase)
            ? "Toaplan Out Zone"
            : $"Toaplan Out Zone ({driverName})";
        RomInfo.ExtraInfo =
            $"MAME set: {driverName}\n" +
            $"Archive: {Path.GetFileName(path)}\n" +
            "Reference: ~/mame/src/mame/toaplan/toaplan1.cpp\n" +
            "Hardware: Toaplan1, 68000 @ 10 MHz, Z80/YM3812 audio, FCU/BCU video, ROT270.";
        RomInfo.RegionHint = ConsoleRegion.Auto;
    }

    private static string GetDriverName(string path)
        => Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();

    private static bool LooksLikeMergedOutZoneArchive(string path)
    {
        try
        {
            using IArchive archive = ArchiveFactory.Open(path);
            var names = new HashSet<string>(
                archive.Entries
                    .Where(static entry => !entry.IsDirectory)
                    .Select(static entry => Path.GetFileName(entry.Key).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            return RequiredMergedSetEntries.All(names.Contains)
                && (names.Contains("tp_018_07.6h") || names.Contains("tp_018_07.6f") || names.Contains("18.6h"))
                && (names.Contains("tp_018_08.6f") || names.Contains("19.6f"));
        }
        catch
        {
            return false;
        }
    }
}
