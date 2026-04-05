using System.IO;
using EutherDrive.Core.GbaEmu;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core;

public sealed class GbaAdapter : IEmulatorCore, ISavestateCapable
{
    private const int FrameWidth = GbaConstants.ScreenWidth;
    private const int FrameHeight = GbaConstants.ScreenHeight;
    private const int FrameStride = FrameWidth * 4;
    private static readonly int OutputSampleRate = ParseOutputSampleRate();

    private readonly object _stateLock = new();
    private Gba? _gba;
    private string? _romPath;
    private string? _savePath;
    private string? _effectiveBiosPath;
    private byte[]? _biosData;
    private int _masterVolumePercent = 100;
    private RomIdentity? _romIdentity;
    private string? _romSummary;
    private short[] _scaledAudioBuffer = Array.Empty<short>();
    private short[] _resampledAudioBuffer = Array.Empty<short>();
    private double _resamplePhase;

    public static string? BiosPath { get; set; }

    public string? RomSummary => _romSummary;
    public string? EffectiveBiosPath => _effectiveBiosPath;
    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _gba?.FrameCounter;
    public ushort? DebugKeyInput => _gba?.Io.KeyInput;

    public double GetTargetFps() => GbaConstants.Fps;

    public void LoadRom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM not found.", path);

        byte[] romData = File.ReadAllBytes(path);
        string saveDirectory = PersistentStoragePath.ResolveSaveDirectory(path, "gba");
        Directory.CreateDirectory(saveDirectory);

        lock (_stateLock)
        {
            var gba = new Gba();
            gba.LoadRom(romData);

            _romPath = path;
            _savePath = Path.Combine(saveDirectory, Path.GetFileNameWithoutExtension(path) + ".sav");
            LoadSaveData(gba, _savePath);

            RefreshBiosSelection();
            gba.Reset();
            ApplyBootBiosMode(gba);
            ResetAudioOutputState();
            gba.Video.RefreshFrame();

            _gba = gba;
            _romIdentity = new RomIdentity(
                Path.GetFileName(path),
                RomIdentity.ComputeSha256(romData),
                PersistentStoragePath.ResolveSavestateDirectory(path, "gba"));
            _romSummary = BuildRomSummary(path, gba, _effectiveBiosPath);
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            if (_gba == null)
                return;

            _gba.Reset();
            ApplyBootBiosMode(_gba);
            ResetAudioOutputState();
            _gba.Video.RefreshFrame();
        }
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            if (_gba == null)
                return;

            _gba.RunFrame();
            FlushSaveDataIfNeeded(_gba);
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        if (_gba == null)
            return ReadOnlySpan<byte>.Empty;
        return _gba.Video.GetFrameBuffer();
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = 2;
        if (_gba == null)
            return ReadOnlySpan<short>.Empty;

        int samples = Math.Min(_gba.Audio.OutputBuffer.Length, _gba.Audio.SamplesWritten * 2);
        if (samples <= 0)
            return ReadOnlySpan<short>.Empty;

        ReadOnlySpan<short> source = _gba.Audio.OutputBuffer.AsSpan(0, samples);
        if (OutputSampleRate == GbaAudio.SampleRate)
        {
            if (_masterVolumePercent >= 100)
                return source;

            EnsureScaledAudioCapacity(samples);
            int scale = _masterVolumePercent;
            for (int i = 0; i < samples; i++)
                _scaledAudioBuffer[i] = (short)((source[i] * scale) / 100);
            return _scaledAudioBuffer.AsSpan(0, samples);
        }

        return ResampleAudio(source);
    }

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
    {
        _ = x;
        _ = y;
        _ = padType;

        if (_gba == null)
            return;

        _gba.SetKeyState(GbaKey.Up, up);
        _gba.SetKeyState(GbaKey.Down, down);
        _gba.SetKeyState(GbaKey.Left, left);
        _gba.SetKeyState(GbaKey.Right, right);
        _gba.SetKeyState(GbaKey.A, a);
        _gba.SetKeyState(GbaKey.B, b);
        _gba.SetKeyState(GbaKey.R, c);
        _gba.SetKeyState(GbaKey.Start, start);
        _gba.SetKeyState(GbaKey.L, z);
        _gba.SetKeyState(GbaKey.Select, mode);
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0)
            percent = 0;
        else if (percent > 100)
            percent = 100;
        _masterVolumePercent = percent;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        lock (_stateLock)
        {
            if (_gba == null)
                throw new InvalidOperationException("GBA core not initialized.");

            const int version = 1;
            writer.Write(version);
            byte[] payload = GbaSerialize.Save(_gba, _gba.Video.CaptureScreenshot());
            writer.Write(payload.Length);
            writer.Write(payload);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_stateLock)
        {
            if (_gba == null)
                throw new InvalidOperationException("GBA core not initialized.");

            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported GBA savestate version: {version}.");

            int length = reader.ReadInt32();
            byte[] payload = reader.ReadBytes(length);
            if (payload.Length != length)
                throw new EndOfStreamException("Unexpected end of GBA savestate.");

            GbaSerialize.Load(_gba, payload);
            ReapplyBiosAfterStateLoad(_gba);
            ResetAudioOutputState();
            _gba.Video.RefreshFrame();
        }
    }

    private void RefreshBiosSelection()
    {
        _effectiveBiosPath = ResolveBiosPath(_romPath);
        _biosData = TryLoadBios(_effectiveBiosPath);
        if (_biosData == null)
            _effectiveBiosPath = null;
    }

    private void ApplyBootBiosMode(Gba gba)
    {
        if (_biosData is { Length: > 0 })
        {
            gba.Bios.UseHle = false;
            gba.Bios.HleActive = false;
            gba.Bios.BiosStall = 0;
            gba.Memory.LoadBios(_biosData);
            gba.Cpu.Reset();
            gba.Video.DispCnt = 0;
            gba.Io.PostFlg = 0;
            gba.CyclesThisFrame = 0;
            gba.FrameCounter = 0;
            gba.TotalCycles = 0;
            gba.IsRunning = true;
        }
        else
        {
            gba.Bios.UseHle = true;
        }
    }

    private void ReapplyBiosAfterStateLoad(Gba gba)
    {
        if (_biosData is { Length: > 0 })
        {
            gba.Bios.UseHle = false;
            gba.Bios.HleActive = false;
            gba.Memory.LoadBios(_biosData);
        }
        else
        {
            gba.Bios.UseHle = true;
        }
    }

    private void LoadSaveData(Gba gba, string? savePath)
    {
        if (gba.Savedata.Data.Length == 0 || string.IsNullOrWhiteSpace(savePath) || !File.Exists(savePath))
            return;

        try
        {
            gba.Savedata.Load(File.ReadAllBytes(savePath));
        }
        catch
        {
            // Keep going with empty save data.
        }
    }

    private void FlushSaveDataIfNeeded(Gba gba)
    {
        if (string.IsNullOrWhiteSpace(_savePath) || gba.Savedata.Data.Length == 0)
            return;

        if (!gba.Savedata.Clean())
            return;

        try
        {
            string? directory = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(_savePath, gba.Savedata.Data);
        }
        catch
        {
            // Ignore save failures for now.
        }
    }

    private void EnsureScaledAudioCapacity(int samples)
    {
        if (_scaledAudioBuffer.Length >= samples)
            return;
        _scaledAudioBuffer = new short[samples];
    }

    private void EnsureResampledAudioCapacity(int samples)
    {
        if (_resampledAudioBuffer.Length >= samples)
            return;
        _resampledAudioBuffer = new short[samples];
    }

    private ReadOnlySpan<short> ResampleAudio(ReadOnlySpan<short> source)
    {
        int sourceFrames = source.Length / 2;
        if (sourceFrames <= 0)
            return ReadOnlySpan<short>.Empty;

        double step = GbaAudio.SampleRate / (double)OutputSampleRate;
        int maxOutputFrames = (int)Math.Ceiling(((sourceFrames + 1) * OutputSampleRate) / (double)GbaAudio.SampleRate) + 2;
        EnsureResampledAudioCapacity(maxOutputFrames * 2);

        double position = _resamplePhase;
        int outFrame = 0;
        int scale = _masterVolumePercent;

        while (position < sourceFrames && outFrame < maxOutputFrames)
        {
            int baseIndex = (int)position;
            double frac = position - baseIndex;
            int nextIndex = baseIndex + 1 < sourceFrames ? baseIndex + 1 : baseIndex;

            int left = InterpolateSample(source, baseIndex * 2, nextIndex * 2, frac);
            int right = InterpolateSample(source, baseIndex * 2 + 1, nextIndex * 2 + 1, frac);

            if (scale < 100)
            {
                left = (left * scale) / 100;
                right = (right * scale) / 100;
            }

            _resampledAudioBuffer[outFrame * 2] = (short)left;
            _resampledAudioBuffer[outFrame * 2 + 1] = (short)right;
            outFrame++;
            position += step;
        }

        _resamplePhase = position - sourceFrames;
        return _resampledAudioBuffer.AsSpan(0, outFrame * 2);
    }

    private static int InterpolateSample(ReadOnlySpan<short> source, int fromIndex, int toIndex, double frac)
    {
        int from = source[fromIndex];
        int to = source[toIndex];
        double value = from + ((to - from) * frac);
        int rounded = (int)Math.Round(value);
        if (rounded > short.MaxValue)
            return short.MaxValue;
        if (rounded < short.MinValue)
            return short.MinValue;
        return rounded;
    }

    private void ResetAudioOutputState()
    {
        _resamplePhase = 0;
    }

    private static int ParseOutputSampleRate()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_OUTPUT_HZ");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value >= 22050
            && value <= 192000)
        {
            return value;
        }

        return 44100;
    }

    private static byte[]? TryLoadBios(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < GbaConstants.BiosSize)
                return null;
            if (data.Length == GbaConstants.BiosSize)
                return data;

            byte[] trimmed = new byte[GbaConstants.BiosSize];
            Buffer.BlockCopy(data, 0, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveBiosPath(string? romPath)
    {
        if (!string.IsNullOrWhiteSpace(BiosPath) && File.Exists(BiosPath))
            return BiosPath;

        var candidateDirectories = new List<string>();
        AddDirectoryCandidate(candidateDirectories, Path.Combine(Directory.GetCurrentDirectory(), "bios"));
        AddDirectoryCandidate(candidateDirectories, Path.Combine(AppContext.BaseDirectory, "bios"));
        if (!string.IsNullOrWhiteSpace(romPath))
        {
            string? romDirectory = Path.GetDirectoryName(romPath);
            if (!string.IsNullOrWhiteSpace(romDirectory))
                AddDirectoryCandidate(candidateDirectories, Path.Combine(romDirectory, "bios"));
        }

        foreach (string directory in candidateDirectories)
        {
            foreach (string name in new[] { "gba_bios.bin", "gba_bios.rom", "gba.bin", "bios.bin" })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                    return path;
            }

            try
            {
                string? wildcardMatch = Directory.EnumerateFiles(directory, "gba*.*")
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(path => IsLikelyGbaBios(path));
                if (!string.IsNullOrWhiteSpace(wildcardMatch))
                    return wildcardMatch;
            }
            catch
            {
                // Ignore directories we cannot enumerate.
            }
        }

        return null;
    }

    private static bool IsLikelyGbaBios(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".rom", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Length >= GbaConstants.BiosSize;
        }
        catch
        {
            return false;
        }
    }

    private static void AddDirectoryCandidate(List<string> candidates, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        if (candidates.Any(existing => string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase)))
            return;
        if (Directory.Exists(directory))
            candidates.Add(directory);
    }

    private static string BuildRomSummary(string path, Gba gba, string? biosPath)
    {
        string name = Path.GetFileName(path);
        string biosSummary = string.IsNullOrWhiteSpace(biosPath)
            ? "BIOS: HLE"
            : $"BIOS: {Path.GetFileName(biosPath)}";
        string saveSummary = gba.Savedata.Type == SavedataType.None
            ? "Save: none"
            : $"Save: {gba.Savedata.Type}";
        string rtcSummary = gba.Hardware.HasRtc ? "RTC" : "No RTC";
        return $"GBA: {name} | {biosSummary} | {saveSummary} | {rtcSummary}";
    }
}
